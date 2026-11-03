using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

public sealed partial class TokenService(
    ApplicationDbContext dbContext,
    IOptions<JwtOptions> jwtOptions,
    IClientInfo clientInfo,
    ILogger<TokenService> logger) : ITokenService, IRefreshTokenPurger
{
    private readonly JwtOptions _options = jwtOptions.Value;

    public async Task<AuthResultDto> IssueTokensAsync(Guid userId, Guid tenantId, TenantRole role, CancellationToken cancellationToken)
    {
        // Iniciar sesión crea una sesión NUEVA (spec 015 R1): dos dispositivos, dos Id, y cerrar uno no toca al otro.
        var sessionId = Guid.NewGuid();
        var accessToken = GenerateAccessToken(userId, tenantId, role, sessionId);

        // Los datos de la sesión (spec 016 R7): cuándo empieza y con qué navegador y desde qué red (truncada). Es el ÚNICO sitio donde se estampan:
        // renovar los hereda, no los vuelve a leer.
        // Un único instante: al iniciar sesión, la fila y la sesión nacen a la vez.
        var startedAt = DateTime.UtcNow;
        var (refreshTokenValue, refreshToken) = CreateRefreshToken(
            userId, tenantId, sessionId, createdAt: startedAt, sessionStartedAt: startedAt, clientInfo.UserAgent, clientInfo.IpAddress);

        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResultDto(accessToken, refreshTokenValue, DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes));
    }

    public async Task<AuthResultDto?> RotateRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(refreshToken);

        var existing = await dbContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);
        if (existing is null)
        {
            LogRefreshTokenRejected(logger, "Unknown", null);
            return null;
        }

        if (existing.RevokedAt is not null)
        {
            // Reutilización de un token ya revocado: posible robo — se revoca toda la
            // cadena activa del usuario (ADR-007).
            var activeTokens = await dbContext.RefreshTokens
                .Where(rt => rt.UserId == existing.UserId && rt.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var token in activeTokens)
            {
                token.Revoke();
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            LogRefreshTokenReuseDetected(logger, existing.UserId, existing.TenantId, activeTokens.Count);
            return null;
        }

        if (!existing.IsActive)
        {
            LogRefreshTokenRejected(logger, "Expired", existing.UserId);
            return null;
        }

        // El rol puede haber cambiado desde que se emitió este token; se relee.
        var tenantUser = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(tu => tu.UserId == existing.UserId && tu.TenantId == existing.TenantId, cancellationToken);

        if (tenantUser is null)
        {
            LogRefreshTokenRejected(logger, "NoTenantMembership", existing.UserId);
            return null;
        }

        // Empresa suspendida (spec 011 R15b): no se renueva la sesión, con la misma respuesta que un token caducado. El token
        // NO se revoca ni se borra: mientras dura la suspensión no sirve, y al reactivar la empresa vuelve a servir si no caducó.
        if (await dbContext.IsSuspendedAsync(existing.TenantId, cancellationToken))
        {
            LogRefreshTokenRejected(logger, "TenantSuspended", existing.UserId);
            return null;
        }

        existing.Revoke();

        // Renovar NO crea una sesión: es la misma (spec 015 R1), así que el token nuevo hereda el SessionId del anterior. El token anterior se
        // revoca y el nuevo se inserta en este mismo SaveChanges (R5): la sesión nunca se queda sin un refresh token activo.
        var accessToken = GenerateAccessToken(existing.UserId, existing.TenantId, tenantUser.Role, existing.SessionId);

        // R8: la fila nueva HEREDA cuándo empezó la sesión, su navegador y su red, y solo toma su propio CreatedAt (la última actividad). Una renovación
        // desde otro navegador o desde otra red NO cambia el dispositivo de la sesión: lo que su dueño reconoce es cómo empezó.
        var (newRefreshTokenValue, newRefreshToken) = CreateRefreshToken(
            existing.UserId, existing.TenantId, existing.SessionId, createdAt: DateTime.UtcNow, sessionStartedAt: existing.SessionStartedAt,
            existing.UserAgent, existing.IpAddress);

        dbContext.RefreshTokens.Add(newRefreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResultDto(accessToken, newRefreshTokenValue, DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes));
    }

    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(refreshToken);

        var existing = await dbContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        // Desconocido, caducado o ya revocado: no se escribe nada ni se registra nada. Y NO se revoca la cadena (a diferencia de
        // /auth/refresh): aquí presentar un token revocado es un cierre, no un uso (spec 013 R3).
        if (existing is null || !existing.IsActive)
        {
            return false;
        }

        // Una sola fila: la de esa sesión. Se revoca y no se borra para que, si alguien usa después este token, /auth/refresh lo
        // detecte como reutilización (ADR-007, spec 013 D3).
        existing.Revoke();
        await dbContext.SaveChangesAsync(cancellationToken);

        LogSessionClosed(logger, existing.UserId, existing.TenantId);
        return true;
    }

    public async Task<int> RevokeAllRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var closed = await RevokeAllActiveAsync(userId, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Un evento por petición, también con cero (R6): «alguien pidió cerrar todo y no había nada» es información.
        LogAllSessionsClosed(logger, userId, closed);
        return closed;
    }

    // Spec 018 D6: la misma revocación pero sin confirmar y sin evento. El restablecimiento de contraseña confirma UNA vez (contraseña, token y sesiones, todo o nada) y deja su propio
    // evento con este recuento; si confirmara aquí, un fallo después dejaría las sesiones cerradas con la contraseña vieja (o al revés).
    public Task<int> RevokeAllRefreshTokensWithoutSavingAsync(Guid userId, CancellationToken cancellationToken) =>
        RevokeAllActiveAsync(userId, cancellationToken);

    private async Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Solo las filas ACTIVAS de esta cuenta (R2): las ya revocadas conservan su fecha y las caducadas no cuentan como sesión. El
        // filtro es siempre por el UserId que llega del token, nunca por un dato de la petición (R1); RefreshTokens no tiene filtro
        // de empresa, así que este Where es lo único que separa a una cuenta de otra.
        var now = DateTime.UtcNow;
        var active = await dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        // Se revoca y no se borra, como en la spec 013: un token cerrado que alguien use después se detecta como reutilización (1003).
        foreach (var token in active)
        {
            token.Revoke();
        }

        return active.Count;
    }

    public async Task<IReadOnlyList<ActiveSession>> ListActiveSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        // R10: solo la cuenta que llega del token (RefreshTokens no tiene filtro de empresa, así que este Where es lo único que separa una cuenta de otra) y solo
        // las filas activas, con la misma definición de «activo» de las specs 013, 014 y 015. Por el índice de UserId.
        var now = DateTime.UtcNow;
        var rows = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > now)
            .OrderByDescending(rt => rt.CreatedAt)
            .ToListAsync(cancellationToken);

        // Una sesión que renovó varias veces tiene UNA fila activa; si una carrera de renovación dejara dos, sale una sola vez, con la más reciente (por el orden de arriba).
        return [.. rows
            .DistinctBy(rt => rt.SessionId)
            .Select(rt => new ActiveSession(rt.SessionId, rt.SessionStartedAt, rt.CreatedAt, rt.UserAgent, rt.IpAddress))];
    }

    public async Task<bool> CloseSessionAsync(Guid userId, Guid sessionId, bool isCurrentSession, CancellationToken cancellationToken)
    {
        // R13: el Id de sesión elige CUÁL de las sesiones de esta cuenta; el UserId (del token, nunca de la petición) forma parte de la condición. Sin él, adivinar
        // el Id de una sesión ajena la cerraría. Por el índice IX_RefreshTokens_SessionId, que ya contiene UserId, RevokedAt y ExpiresAt.
        var now = DateTime.UtcNow;
        var active = await dbContext.RefreshTokens
            .Where(rt => rt.SessionId == sessionId && rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        // R14: no existe, es de otra cuenta o ya estaba cerrada: no se distingue, no se escribe nada y no hay evento.
        if (active.Count == 0)
        {
            return false;
        }

        // Se revoca y no se borra, como en las specs 013 y 014: un token cerrado que alguien use después se detecta como reutilización (1003).
        foreach (var token in active)
        {
            token.Revoke();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        LogSessionClosedByOwner(logger, userId, isCurrentSession);
        return true;
    }

    public Task<bool> IsSessionActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        // La misma definición de «activo» que RefreshToken.IsActive y que los cierres de las specs 013 y 014 (sin revocar y sin caducar).
        // Se filtra por el SessionId Y por el UserId del token ya validado: RefreshTokens no tiene filtro de empresa, y sin el UserId el Id de
        // sesión de una cuenta valdría en el token de otra. Un solo AnyAsync que el índice IX_RefreshTokens_SessionId responde entero.
        var now = DateTime.UtcNow;
        return dbContext.RefreshTokens
            .AsNoTracking()
            .AnyAsync(rt => rt.SessionId == sessionId && rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > now, cancellationToken);
    }

    public Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken) =>
        // Set-based y en LINQ (no SQL a mano: ADR-009 R1 limita el SQL fuera de LINQ a AccountLockout). Lo MÁS antiguo primero y con tope de
        // lote: un solo DELETE gigante bloquearía una tabla que se consulta en cada petición. Solo mira ExpiresAt (índice IX_RefreshTokens_ExpiresAt):
        // revocada o no da igual, porque una fila caducada hace más del margen ya no puede usarse (R1). Una sesión activa tiene su fila sin
        // caducar, así que nunca cumple esta condición (R2).
        dbContext.RefreshTokens
            .Where(rt => rt.ExpiresAt < cutoff)
            .OrderBy(rt => rt.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task DeleteRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .ToListAsync(cancellationToken);

        // Sin SaveChangesAsync: el Handler lo confirma junto con el resto de la baja (spec 009 R14).
        dbContext.RefreshTokens.RemoveRange(tokens);
    }

    private string GenerateAccessToken(Guid userId, Guid tenantId, TenantRole role, Guid sessionId)
    {
        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(OfiFlowClaimTypes.TenantId, tenantId.ToString()),
            new Claim(OfiFlowClaimTypes.Role, role.ToString()),
            new Claim(OfiFlowClaimTypes.SessionId, sessionId.ToString())
        ];

        var key = new SymmetricSecurityKey(Convert.FromBase64String(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private (string Value, RefreshToken Entity) CreateRefreshToken(
        Guid userId, Guid tenantId, Guid sessionId, DateTime createdAt, DateTime sessionStartedAt, string? userAgent, string? ipAddress)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var entity = new RefreshToken(
            userId, tenantId, sessionId, Hash(value), createdAt.AddDays(_options.RefreshTokenDays),
            createdAt: createdAt, sessionStartedAt: sessionStartedAt, userAgent: userAgent, ipAddress: ipAddress);

        return (value, entity);
    }

    private static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Eventos de seguridad (ADR-009 R5): nunca incluyen el valor ni el hash del token.

    [LoggerMessage(EventId = SecurityEventIds.RefreshTokenReuseDetected, Level = LogLevel.Warning,
        Message = "Reutilización de refresh token revocado (posible robo). UserId {UserId}, TenantId {TenantId}; revocados {RevokedCount} tokens activos")]
    private static partial void LogRefreshTokenReuseDetected(ILogger logger, Guid userId, Guid tenantId, int revokedCount);

    [LoggerMessage(EventId = SecurityEventIds.SessionClosed, Level = LogLevel.Information,
        Message = "Sesión cerrada. UserId {UserId}, TenantId {TenantId}")]
    private static partial void LogSessionClosed(ILogger logger, Guid userId, Guid tenantId);

    [LoggerMessage(EventId = SecurityEventIds.AllSessionsClosed, Level = LogLevel.Warning,
        Message = "Todas las sesiones cerradas a petición de su dueño. UserId {UserId}; sesiones cerradas {ClosedCount}")]
    private static partial void LogAllSessionsClosed(ILogger logger, Guid userId, int closedCount);

    // R17: el UserId y si era la sesión actual. NUNCA el Id de la sesión cerrada, ni tokens ni hashes.
    [LoggerMessage(EventId = SecurityEventIds.SessionClosedByOwner, Level = LogLevel.Warning,
        Message = "Una sesión cerrada por su dueño. UserId {UserId}; era la sesión actual: {WasCurrent}")]
    private static partial void LogSessionClosedByOwner(ILogger logger, Guid userId, bool wasCurrent);

    [LoggerMessage(EventId = SecurityEventIds.RefreshTokenRejected, Level = LogLevel.Warning,
        Message = "Refresh token rechazado. Motivo {Reason}, UserId {UserId}")]
    private static partial void LogRefreshTokenRejected(ILogger logger, string reason, Guid? userId);
}

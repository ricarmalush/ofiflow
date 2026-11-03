using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Los tokens de restablecimiento de contraseña (spec 018, ADR-021). <b>Este es el único fichero que lee la tabla</b> (como <see cref="TokenService"/> con los
/// refresh tokens): un test de arquitectura lo vigila. Los tokens se buscan por su hash, que es secreto y único, y no tienen filtro de empresa porque son de una
/// cuenta (R17).
/// </summary>
/// <remarks>
/// Hace cuatro cosas: la <b>purga</b> de los caducados (R18), el <b>borrado al dar de baja a una cuenta</b>, <b>pedir un token</b> (bloque 2) y <b>canjearlo</b> (bloque 3).
/// </remarks>
internal sealed class PasswordResetService(ApplicationDbContext dbContext, TimeProvider timeProvider) : IPasswordResetService, IPasswordResetTokenPurger
{
    public async Task<PasswordResetIssue> IssueAsync(string email, CancellationToken cancellationToken)
    {
        // La búsqueda por el email normalizado, la misma regla que el registro y el login (ADR-004): «Ana@Example.com» y «ana@example.com» son la misma cuenta.
        var normalizedEmail = email.ToUpperInvariant();
        var userId = await dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return new PasswordResetIssue(PasswordResetIssueOutcome.UnknownAccount);
        }

        // R14: una cuenta de operador de la plataforma es otra identidad (spec 011): se trata como «no existe», esté activo o dado de baja. Su contraseña se gestiona
        // con la consola de administración del servidor, no por correo.
        if (await dbContext.PlatformOperators.AnyAsync(o => o.UserId == userId, cancellationToken))
        {
            return new PasswordResetIssue(PasswordResetIssueOutcome.UnknownAccount);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // R4: como mucho MaxPerAccountPerHour tokens por cuenta en la última hora, vengan de la IP que vengan. Se cuentan los CREADOS (usados o no): pedir, usar y volver a pedir
        // también cuenta, porque lo que se limita es el correo que le llega a la persona.
        var since = now - TimeSpan.FromHours(1);
        var recent = await dbContext.PasswordResetTokens.CountAsync(t => t.UserId == userId && t.CreatedAt > since, cancellationToken);
        if (recent >= PasswordResetToken.MaxPerAccountPerHour)
        {
            return new PasswordResetIssue(PasswordResetIssueOutcome.Throttled, userId);
        }

        // R3: un solo token vigente por cuenta. Pedir otro anula los anteriores: el enlace del correo viejo deja de valer.
        var active = await dbContext.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        foreach (var token in active)
        {
            token.Invalidate(now);
        }

        var (value, hash) = OpaqueToken.Generate();
        dbContext.PasswordResetTokens.Add(PasswordResetToken.Create(userId.Value, hash, now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Una carrera con alguien que usa el token anterior justo ahora (la versión de fila cambió). Responder con un error 500 aquí solo ocurriría con cuentas que existen: un
            // oráculo raro pero real. Se trata como «ahora no»: no se crea nada y la persona puede volver a pedirlo.
            return new PasswordResetIssue(PasswordResetIssueOutcome.Throttled, userId);
        }

        // El valor en claro sale de aquí UNA vez, para el correo. En la base de datos solo queda su hash.
        return new PasswordResetIssue(PasswordResetIssueOutcome.Issued, userId, value);
    }

    public async Task<PasswordResetConsumption> ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        // Por el hash: el token en claro no se guarda en ningún sitio y el hash es único (índice) y de 256 bits, así que esta búsqueda no distingue cuentas ni empresas (R17).
        var hash = OpaqueToken.Hash(token);
        var entity = await dbContext.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        // R8: los motivos reales solo viajan hasta el registro; el caso de uso responde lo mismo para todos.
        if (entity is null)
        {
            return Rejected("Unknown");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // «Usado» y «anulado por otro pedido» dejan la misma marca (UsedAt, como en el dominio: Invalidate no distingue), y se cuentan juntos.
        if (entity.UsedAt is not null)
        {
            return Rejected("UsedOrReplaced", entity.UserId);
        }

        if (!entity.IsUsableAt(now))
        {
            return Rejected("Expired", entity.UserId);
        }

        // R14: la cuenta de un operador nunca tiene token (IssueAsync no lo crea), pero esta puerta no depende de eso: si una cuenta pasara a ser de operador con un token vivo,
        // su contraseña se gestiona con la consola de administración, no por correo.
        if (await dbContext.PlatformOperators.AnyAsync(o => o.UserId == entity.UserId, cancellationToken))
        {
            return Rejected("PlatformOperator", entity.UserId);
        }

        // R10: se marca como usado SIN confirmar. La confirmación es la del caso de uso y la decide la versión de fila: dos canjes a la vez leyeron «sin usar», los dos llegan aquí,
        // y solo uno de los dos UPDATE encuentra la fila como la leyó.
        entity.Consume(now);

        // R3/R9b: el resto de los vigentes de la cuenta también dejan de valer (normalmente no queda ninguno, porque pedir otro ya anula el anterior).
        var others = await dbContext.PasswordResetTokens
            .Where(t => t.UserId == entity.UserId && t.Id != entity.Id && t.UsedAt == null && t.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            other.Invalidate(now);
        }

        return new PasswordResetConsumption(PasswordResetConsumeOutcome.Consumed, entity.UserId);
    }

    private static PasswordResetConsumption Rejected(string reason, Guid? userId = null) =>
        new(PasswordResetConsumeOutcome.Rejected, userId, reason);

    public Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken) =>
        // Set-based y en LINQ (no SQL a mano: ADR-009 R1). Lo MÁS antiguo primero y con tope de lote. Solo mira ExpiresAt (índice IX_PasswordResetTokens_ExpiresAt):
        // usado o no da igual, porque un token caducado hace más del margen ya no puede usarse. Un token vigente no ha caducado, así que nunca cumple esta condición.
        dbContext.PasswordResetTokens
            .Where(t => t.ExpiresAt < cutoff)
            .OrderBy(t => t.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Borra todos los tokens de una cuenta que se da de baja (spec 009 R14, como <c>MfaStore.RemoveAllAsync</c>): la tabla no tiene clave foránea hacia la
    /// cuenta, así que no se borrarían solos y quedarían filas huérfanas. <b>No confirma el cambio</b>: lo hace el Handler en su único <c>SaveChangesAsync</c>, junto con
    /// el resto de la baja, para que sea todo o nada.
    /// </summary>
    internal static async Task RemoveAllAsync(ApplicationDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.PasswordResetTokens
            .Where(t => t.UserId == userId)
            .ToListAsync(cancellationToken);

        dbContext.PasswordResetTokens.RemoveRange(tokens);
    }
}

using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

public class TokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    private static (ApplicationDbContext Db, TokenService Service) CreateSut(ILogger<TokenService>? logger = null)
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ApplicationDbContext(dbOptions, new FixedTenantContext(Guid.NewGuid()));
        var service = new TokenService(
            db,
            Microsoft.Extensions.Options.Options.Create(Options),
            logger ?? NullLogger<TokenService>.Instance);

        return (db, service);
    }

    [Fact]
    public async Task IssueTokensAsync_ReturnsAccessTokenWithTenantClaim()
    {
        var (_, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var result = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal(tenantId.ToString(), jwt.Claims.First(c => c.Type == "tenant_id").Value);
        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task IssueTokensAsync_WritesTheRoleNameInTheRoleClaim(TenantRole role)
    {
        // El nombre "role" y el valor (el nombre del rol) son contrato con CurrentUser (ADR-012 R4):
        // si la librería de JWT cambiara cómo acorta el tipo de claim, la autorización dejaría de ver el rol.
        var (_, service) = CreateSut();

        var result = await service.IssueTokensAsync(Guid.NewGuid(), Guid.NewGuid(), role, CancellationToken.None);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal(role.ToString(), jwt.Claims.Single(c => c.Type == "role").Value);
    }

    [Fact]
    public async Task RotateRefreshTokenAsync_WithValidToken_ReturnsNewTokensAndRevokesTheOld()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // RotateRefreshTokenAsync relee el rol en TenantUsers en cada refresh (defensa en
        // profundidad: el usuario podría haber dejado de pertenecer al tenant) — hace falta
        // esta fila para que la rotación encuentre membresía válida.
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var rotated = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.NotEqual(issued.RefreshToken, rotated!.RefreshToken);

        var allTokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, allTokens.Count);
        Assert.Contains(allTokens, t => t.RevokedAt != null);
    }

    [Fact]
    public async Task RotateRefreshTokenAsync_WithAlreadyRevokedToken_RevokesWholeChainAndReturnsNull()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var firstRotation = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);
        Assert.NotNull(firstRotation);

        // Reutilizar el token original (ya revocado) simula un token robado.
        var reuseAttempt = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.Null(reuseAttempt);

        var allTokens = await db.RefreshTokens.ToListAsync();
        Assert.All(allTokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RotateRefreshTokenAsync_WithAlreadyRevokedToken_LogsSecurityEventWithoutTheToken()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokenReuseDetected);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(userId.ToString(), entry.Message);
        Assert.DoesNotContain(issued.RefreshToken, entry.Message);
    }

    // ===== Cerrar sesión (spec 013) =====

    [Fact]
    public async Task Revoke_OfAnActiveToken_RevokesThatOneRow_AndTheTokenNoLongerRotates()
    {
        var (db, service) = CreateSut();
        var session = await SeedSessionAsync(db, service, suspended: false);

        var closed = await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.True(closed);
        var stored = await db.RefreshTokens.SingleAsync(); // se revoca, no se borra (D3)
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task Revoke_ClosesOnlyThatSession_TheOtherDeviceOfTheSameAccountKeepsWorking()
    {
        var (db, service) = CreateSut();
        var session = await SeedSessionAsync(db, service, suspended: false);
        var otherDevice = await service.IssueTokensAsync(session.UserId, session.Tenant.Id, TenantRole.Owner, CancellationToken.None);

        await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.Single(await db.RefreshTokens.Where(t => t.RevokedAt == null).ToListAsync()); // solo queda activa la del otro dispositivo
        Assert.NotEqual(session.RefreshToken, otherDevice.RefreshToken);
        Assert.NotNull(await service.RotateRefreshTokenAsync(otherDevice.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_ThenRotateWithTheSameToken_IsTreatedAsReuse_AndRevokesTheChain()
    {
        // R6: un token que el cliente legítimo ya cerró y alguien usa después es, por definición, una copia.
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var session = await SeedSessionAsync(db, service, suspended: false);
        var otherDevice = await service.IssueTokensAsync(session.UserId, session.Tenant.Id, TenantRole.Owner, CancellationToken.None);
        await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        var reuse = await service.RotateRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.Null(reuse);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokenReuseDetected);
        Assert.Null(await service.RotateRefreshTokenAsync(otherDevice.RefreshToken, CancellationToken.None)); // la cadena cayó
    }

    [Fact]
    public async Task Revoke_OfAnUnknownToken_ChangesNothing_AndLogsNothing()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var session = await SeedSessionAsync(db, service, suspended: false);

        var closed = await service.RevokeRefreshTokenAsync("token-que-no-existe", CancellationToken.None);

        Assert.False(closed);
        Assert.Null((await db.RefreshTokens.SingleAsync()).RevokedAt);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Revoke_OfAnAlreadyRevokedToken_ChangesNothing_AndDoesNotRevokeTheChain()
    {
        // R3: cerrar con un token ya rotado NO es una reutilización; los demás tokens de la cuenta siguen vivos.
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var session = await SeedSessionAsync(db, service, suspended: false);
        var otherDevice = await service.IssueTokensAsync(session.UserId, session.Tenant.Id, TenantRole.Owner, CancellationToken.None);
        await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);
        var revokedAt = (await db.RefreshTokens.SingleAsync(t => t.RevokedAt != null)).RevokedAt;
        logger.Entries.Clear();

        var closedAgain = await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.False(closedAgain);
        Assert.Equal(revokedAt, (await db.RefreshTokens.SingleAsync(t => t.RevokedAt != null)).RevokedAt); // la fecha no se pisa
        Assert.Single(await db.RefreshTokens.Where(t => t.RevokedAt == null).ToListAsync()); // el otro dispositivo sigue activo
        Assert.Empty(logger.Entries);
        Assert.NotNull(await service.RotateRefreshTokenAsync(otherDevice.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_OfAnExpiredToken_ChangesNothing_AndLogsNothing()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var value = "token-caducado";
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        db.RefreshTokens.Add(new RefreshToken(userId, tenantId, hash, DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync(CancellationToken.None);

        var closed = await service.RevokeRefreshTokenAsync(value, CancellationToken.None);

        Assert.False(closed);
        Assert.Null((await db.RefreshTokens.SingleAsync()).RevokedAt);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Revoke_OfAnActiveToken_LogsOneSecurityEvent_WithIdsOnly_NeverTheTokenNorItsHash()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var session = await SeedSessionAsync(db, service, suspended: false);
        var hash = (await db.RefreshTokens.SingleAsync()).TokenHash;

        await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.SessionClosed);
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(session.UserId.ToString(), entry.Message);
        Assert.Contains(session.Tenant.Id.ToString(), entry.Message);
        Assert.DoesNotContain(session.RefreshToken, entry.Message);
        Assert.DoesNotContain(hash, entry.Message);
    }

    [Fact]
    public async Task Revoke_OfASuspendedCompanysToken_StillClosesIt()
    {
        // R5: cerrar sesión no se bloquea por la empresa.
        var (db, service) = CreateSut();
        var session = await SeedSessionAsync(db, service, suspended: true);

        var closed = await service.RevokeRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.True(closed);
        Assert.NotNull((await db.RefreshTokens.SingleAsync()).RevokedAt);
    }

    // ===== Empresa suspendida (spec 011 R15b) =====

    /// <summary>Una empresa real (con su fila, para que la suspensión pueda leerse) y un miembro suyo con sesión emitida.</summary>
    private static async Task<(Tenant Tenant, Guid UserId, string RefreshToken)> SeedSessionAsync(
        ApplicationDbContext db, TokenService service, bool suspended)
    {
        var tenant = Tenant.Create("Fontanería Pérez");
        if (suspended)
        {
            tenant.Suspend(new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc));
        }

        var userId = Guid.NewGuid();
        db.Tenants.Add(tenant);
        db.TenantUsers.Add(TenantUser.CreateOwner(tenant.Id, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await service.IssueTokensAsync(userId, tenant.Id, TenantRole.Owner, CancellationToken.None);

        return (tenant, userId, issued.RefreshToken);
    }

    [Fact]
    public async Task Rotate_OfASuspendedCompany_ReturnsNull_ButDoesNotRevokeNorDeleteTheToken()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var session = await SeedSessionAsync(db, service, suspended: false);
        session.Tenant.Suspend(new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(CancellationToken.None);

        var rotated = await service.RotateRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.Null(rotated);
        var stored = await db.RefreshTokens.SingleAsync();
        Assert.Null(stored.RevokedAt); // no se revoca: mientras dura la suspensión no sirve, y al reactivar vuelve a servir
        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokenRejected);
        Assert.Contains("TenantSuspended", entry.Message);
        Assert.Contains(session.UserId.ToString(), entry.Message);
        Assert.DoesNotContain(session.RefreshToken, entry.Message);
    }

    [Fact]
    public async Task Rotate_AfterTheCompanyIsReactivated_WorksWithTheSameToken()
    {
        var (db, service) = CreateSut();
        var session = await SeedSessionAsync(db, service, suspended: true);
        Assert.Null(await service.RotateRefreshTokenAsync(session.RefreshToken, CancellationToken.None)); // control: suspendida, no sirve

        session.Tenant.Reactivate();
        await db.SaveChangesAsync(CancellationToken.None);
        var rotated = await service.RotateRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.NotNull(rotated);
    }

    [Fact]
    public async Task Rotate_OfAnActiveCompany_IsNotAffectedByAnotherCompanyBeingSuspended()
    {
        var (db, service) = CreateSut();
        await SeedSessionAsync(db, service, suspended: true);
        var active = await SeedSessionAsync(db, service, suspended: false);

        var rotated = await service.RotateRefreshTokenAsync(active.RefreshToken, CancellationToken.None);

        Assert.NotNull(rotated);
    }
}

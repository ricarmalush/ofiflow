using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
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
        db.RefreshTokens.Add(new RefreshToken(userId, tenantId, Guid.NewGuid(), hash, DateTime.UtcNow.AddMinutes(-1)));
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

    // ===== Cerrar todas las sesiones (spec 014) =====

    [Fact]
    public async Task RevokeAll_RevokesEveryActiveSessionOfTheAccount_AndNoneCanRotateAnymore()
    {
        var (db, service) = CreateSut();
        var first = await SeedSessionAsync(db, service, suspended: false);
        var second = await service.IssueTokensAsync(first.UserId, first.Tenant.Id, TenantRole.Owner, CancellationToken.None);
        var third = await service.IssueTokensAsync(first.UserId, first.Tenant.Id, TenantRole.Owner, CancellationToken.None);

        var closed = await service.RevokeAllRefreshTokensAsync(first.UserId, CancellationToken.None);

        Assert.Equal(3, closed);
        Assert.All(await db.RefreshTokens.ToListAsync(), token => Assert.NotNull(token.RevokedAt));
        Assert.Null(await service.RotateRefreshTokenAsync(first.RefreshToken, CancellationToken.None));
        Assert.Null(await service.RotateRefreshTokenAsync(second.RefreshToken, CancellationToken.None));
        Assert.Null(await service.RotateRefreshTokenAsync(third.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeAll_RevokesNotDeletes_TheRowsRemain()
    {
        // R3: si se borraran, el token de un ladrón pasaría a ser «desconocido» y se perdería la señal de reutilización.
        var (db, service) = CreateSut();
        var session = await SeedSessionAsync(db, service, suspended: false);
        await service.IssueTokensAsync(session.UserId, session.Tenant.Id, TenantRole.Owner, CancellationToken.None);

        await service.RevokeAllRefreshTokensAsync(session.UserId, CancellationToken.None);

        Assert.Equal(2, await db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task RevokeAll_DoesNotTouchTheSessionsOfAnotherAccount()
    {
        // R1: RefreshTokens no tiene filtro de empresa; el Where por UserId es lo único que separa una cuenta de otra.
        var (db, service) = CreateSut();
        var ana = await SeedSessionAsync(db, service, suspended: false);
        var luis = await SeedSessionAsync(db, service, suspended: false);

        var closed = await service.RevokeAllRefreshTokensAsync(ana.UserId, CancellationToken.None);

        Assert.Equal(1, closed);
        Assert.Single(await db.RefreshTokens.Where(t => t.RevokedAt != null).ToListAsync());
        Assert.NotNull(await service.RotateRefreshTokenAsync(luis.RefreshToken, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeAll_ReachesTheSessionsOfEveryCompanyOfTheAccount()
    {
        // R2: una cuenta con sesiones en varias empresas las pierde todas.
        var (db, service) = CreateSut();
        var session = await SeedSessionAsync(db, service, suspended: false);
        await service.IssueTokensAsync(session.UserId, Guid.NewGuid(), TenantRole.Technician, CancellationToken.None);

        var closed = await service.RevokeAllRefreshTokensAsync(session.UserId, CancellationToken.None);

        Assert.Equal(2, closed);
        Assert.DoesNotContain(await db.RefreshTokens.ToListAsync(), token => token.RevokedAt is null);
    }

    [Fact]
    public async Task RevokeAll_DoesNotOverwriteTheDateOfAnAlreadyRevokedToken_NorCountsIt()
    {
        var (db, service) = CreateSut();
        var closedBefore = await SeedSessionAsync(db, service, suspended: false);
        await service.RevokeRefreshTokenAsync(closedBefore.RefreshToken, CancellationToken.None);
        var alreadyRevoked = await db.RefreshTokens.SingleAsync();
        var revokedAt = alreadyRevoked.RevokedAt;
        await service.IssueTokensAsync(closedBefore.UserId, closedBefore.Tenant.Id, TenantRole.Owner, CancellationToken.None);
        await Task.Delay(20); // que la hora de ahora no pueda coincidir con la de la revocación anterior

        var closed = await service.RevokeAllRefreshTokensAsync(closedBefore.UserId, CancellationToken.None);

        Assert.Equal(1, closed); // solo la activa
        Assert.Equal(revokedAt, alreadyRevoked.RevokedAt); // EF devuelve la misma fila rastreada: si se tocara, cambiaría
    }

    [Fact]
    public async Task RevokeAll_DoesNotCountNorTouchExpiredTokens()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        db.RefreshTokens.Add(new RefreshToken(userId, Guid.NewGuid(), Guid.NewGuid(), "hash-caducado", DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync(CancellationToken.None);

        var closed = await service.RevokeAllRefreshTokensAsync(userId, CancellationToken.None);

        Assert.Equal(0, closed);
        Assert.Null((await db.RefreshTokens.SingleAsync()).RevokedAt); // una sesión caducada no es una sesión que cerrar
    }

    [Fact]
    public async Task RevokeAll_WithNoActiveSessions_ReturnsZero_AndStillLeavesTheEventWithZero()
    {
        // R6: «alguien pidió cerrar todo y no había nada» también es información.
        var logger = new ListLogger<TokenService>();
        var (_, service) = CreateSut(logger);
        var userId = Guid.NewGuid();

        var closed = await service.RevokeAllRefreshTokensAsync(userId, CancellationToken.None);

        Assert.Equal(0, closed);
        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.AllSessionsClosed);
        Assert.Contains(userId.ToString(), entry.Message);
        Assert.Contains("sesiones cerradas 0", entry.Message); // el texto entero: un «0» suelto podría salir del Guid
    }

    [Fact]
    public async Task RevokeAll_LogsOneEvent_WithTheUserAndTheCount_NeverTheTokensNorTheirHashes()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var first = await SeedSessionAsync(db, service, suspended: false);
        var second = await service.IssueTokensAsync(first.UserId, first.Tenant.Id, TenantRole.Owner, CancellationToken.None);
        var hashes = (await db.RefreshTokens.ToListAsync()).Select(t => t.TokenHash).ToList();

        await service.RevokeAllRefreshTokensAsync(first.UserId, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(SecurityEventIds.AllSessionsClosed, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(first.UserId.ToString(), entry.Message);
        Assert.Contains("sesiones cerradas 2", entry.Message); // el texto entero: un «2» suelto podría salir del Guid
        Assert.DoesNotContain(first.RefreshToken, entry.Message);
        Assert.DoesNotContain(second.RefreshToken, entry.Message);
        Assert.All(hashes, hash => Assert.DoesNotContain(hash, entry.Message));
    }

    [Fact]
    public async Task RevokeAll_ThenRotateWithAClosedToken_IsTreatedAsReuse()
    {
        // R3: un token cerrado que alguien usa después es, por definición, una copia.
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var session = await SeedSessionAsync(db, service, suspended: false);
        await service.RevokeAllRefreshTokensAsync(session.UserId, CancellationToken.None);

        var reuse = await service.RotateRefreshTokenAsync(session.RefreshToken, CancellationToken.None);

        Assert.Null(reuse);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokenReuseDetected);
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

    // ---- El Id de sesión (spec 015, bloque 1) ----

    private static Guid SidOf(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(c => c.Type == "sid").Value);

    [Fact]
    public async Task IssueTokens_WritesTheSidClaim_AndItIsTheSessionIdOfTheRefreshToken()
    {
        // El nombre "sid" es contrato con quien valida el token (spec 015 R1), como "tenant_id" y "role".
        var (db, service) = CreateSut();

        var issued = await service.IssueTokensAsync(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner, CancellationToken.None);

        var row = await db.RefreshTokens.SingleAsync();
        Assert.Equal(row.SessionId, SidOf(issued.AccessToken));
        Assert.NotEqual(Guid.Empty, row.SessionId);
    }

    [Fact]
    public async Task IssueTokens_EveryLoginIsANewSession_EvenForTheSameAccountAndCompany()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var first = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var second = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        Assert.NotEqual(SidOf(first.AccessToken), SidOf(second.AccessToken));
        Assert.Equal(2, (await db.RefreshTokens.Select(rt => rt.SessionId).Distinct().ToListAsync()).Count);
    }

    [Fact]
    public async Task Rotate_KeepsTheSession_TheNewAccessTokenAndTheNewRefreshTokenCarryTheSameId()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);
        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var rotated = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.NotNull(rotated);
        var original = SidOf(issued.AccessToken);
        Assert.Equal(original, SidOf(rotated!.AccessToken));
        Assert.All(await db.RefreshTokens.ToListAsync(), rt => Assert.Equal(original, rt.SessionId));
    }

    [Fact]
    public async Task Rotate_ManyTimes_StillTheSameSession()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);
        var current = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var original = SidOf(current.AccessToken);

        for (var i = 0; i < 3; i++)
        {
            current = (await service.RotateRefreshTokenAsync(current.RefreshToken, CancellationToken.None))!;
            Assert.Equal(original, SidOf(current.AccessToken));
        }

        Assert.Equal(4, await db.RefreshTokens.CountAsync());
        Assert.Equal(original, (await db.RefreshTokens.Select(rt => rt.SessionId).Distinct().SingleAsync()));
    }

    [Fact]
    public async Task Rotate_OneDevice_DoesNotChangeTheSessionOfTheOtherDevice()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);
        var phone = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var laptop = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var rotatedPhone = await service.RotateRefreshTokenAsync(phone.RefreshToken, CancellationToken.None);

        Assert.Equal(SidOf(phone.AccessToken), SidOf(rotatedPhone!.AccessToken));
        Assert.NotEqual(SidOf(laptop.AccessToken), SidOf(rotatedPhone.AccessToken));
    }

    // ---- ¿Sigue activa la sesión? (spec 015 R2, bloque 2) ----

    private static async Task<(Guid UserId, Guid TenantId, AuthResultDto Issued)> OpenSessionAsync(ApplicationDbContext db, TokenService service)
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        return (userId, tenantId, await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_ForAFreshSession_IsTrue()
    {
        var (db, service) = CreateSut();
        var (userId, _, issued) = await OpenSessionAsync(db, service);

        Assert.True(await service.IsSessionActiveAsync(userId, SidOf(issued.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_AfterTheSessionIsClosedWithLogout_IsFalse()
    {
        var (db, service) = CreateSut();
        var (userId, _, issued) = await OpenSessionAsync(db, service);

        await service.RevokeRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.False(await service.IsSessionActiveAsync(userId, SidOf(issued.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_AfterCloseAll_IsFalseForEverySession()
    {
        var (db, service) = CreateSut();
        var (userId, tenantId, first) = await OpenSessionAsync(db, service);
        var second = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        await service.RevokeAllRefreshTokensAsync(userId, CancellationToken.None);

        Assert.False(await service.IsSessionActiveAsync(userId, SidOf(first.AccessToken), CancellationToken.None));
        Assert.False(await service.IsSessionActiveAsync(userId, SidOf(second.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_AfterTheTokensAreDeleted_IsFalse()
    {
        // La baja de un miembro y el segundo factor BORRAN los refresh tokens (no los revocan): también cierran la sesión.
        var (db, service) = CreateSut();
        var (userId, _, issued) = await OpenSessionAsync(db, service);

        await service.DeleteRefreshTokensAsync(userId, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.False(await service.IsSessionActiveAsync(userId, SidOf(issued.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_AfterARotation_StaysTrue_BecauseTheNewRowIsOfTheSameSession()
    {
        // R5: la renovación revoca el token anterior, pero el nuevo es de la MISMA sesión, así que la sesión (y el access token anterior,
        // que lleva su Id) sigue activa.
        var (db, service) = CreateSut();
        var (userId, _, issued) = await OpenSessionAsync(db, service);

        await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.True(await service.IsSessionActiveAsync(userId, SidOf(issued.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_WhenTheOnlyRowIsExpired_IsFalse()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.RefreshTokens.Add(new RefreshToken(userId, Guid.NewGuid(), sessionId, "hash-caducado", DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.False(await service.IsSessionActiveAsync(userId, sessionId, CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_ForAnotherAccountsSessionId_IsFalse()
    {
        // El Id de sesión de una cuenta no vale en el token de otra: sin el UserId en la pregunta, serviría.
        var (db, service) = CreateSut();
        var (_, _, issued) = await OpenSessionAsync(db, service);

        Assert.False(await service.IsSessionActiveAsync(Guid.NewGuid(), SidOf(issued.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_ForAnUnknownSession_IsFalse()
    {
        var (db, service) = CreateSut();
        var (userId, _, _) = await OpenSessionAsync(db, service);

        Assert.False(await service.IsSessionActiveAsync(userId, Guid.NewGuid(), CancellationToken.None));
        Assert.False(await service.IsSessionActiveAsync(userId, Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_ClosingOneSession_LeavesTheOtherOfTheSameAccountActive()
    {
        var (db, service) = CreateSut();
        var (userId, tenantId, laptop) = await OpenSessionAsync(db, service);
        var phone = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        await service.RevokeRefreshTokenAsync(laptop.RefreshToken, CancellationToken.None);

        Assert.False(await service.IsSessionActiveAsync(userId, SidOf(laptop.AccessToken), CancellationToken.None));
        Assert.True(await service.IsSessionActiveAsync(userId, SidOf(phone.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task IsSessionActive_OnlyReads_ItChangesNothing()
    {
        var (db, service) = CreateSut();
        var (userId, _, issued) = await OpenSessionAsync(db, service);
        var before = await db.RefreshTokens.AsNoTracking().Select(rt => new { rt.Id, rt.RevokedAt }).ToListAsync();

        await service.IsSessionActiveAsync(userId, SidOf(issued.AccessToken), CancellationToken.None);

        Assert.Equal(before, await db.RefreshTokens.AsNoTracking().Select(rt => new { rt.Id, rt.RevokedAt }).ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries().Where(e => e.State is not EntityState.Unchanged and not EntityState.Detached));
    }
}

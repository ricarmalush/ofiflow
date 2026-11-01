using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Listar y cerrar sesiones concretas (spec 016, bloque 3) en el servicio de tokens, con una base en memoria. Cada caso de «se cierra» va con su control de
/// «no se cierra»: lo que más importa es que el <c>UserId</c> forma parte de la condición (adivinar el Id de una sesión ajena no la cierra).
/// </summary>
public class TokenServiceSessionsTests
{
    private static readonly JwtOptions Options = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    private static (ApplicationDbContext Db, TokenService Service) CreateSut(ILogger<TokenService>? logger = null, IClientInfo? client = null)
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new ApplicationDbContext(dbOptions, new FixedTenantContext(Guid.NewGuid()));

        return (db, new TokenService(db, Microsoft.Extensions.Options.Options.Create(Options), client ?? new FakeClientInfo(), logger ?? NullLogger<TokenService>.Instance));
    }

    private static Guid SidOf(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(c => c.Type == "sid").Value);

    private static async Task<(Guid UserId, Guid TenantId)> SeedAccountAsync(ApplicationDbContext db)
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        return (userId, tenantId);
    }

    // ===== Listar =====

    [Fact]
    public async Task ListActive_ReturnsOneEntryPerSession_WithTheDataOfTheActiveRow()
    {
        var client = new FakeClientInfo("203.0.113.0", "Chrome/120 (Windows)");
        var (db, service) = CreateSut(client: client);
        var (userId, tenantId) = await SeedAccountAsync(db);
        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var started = (await db.RefreshTokens.SingleAsync()).SessionStartedAt;
        await Task.Delay(30);
        await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None); // renueva: ahora hay una fila revocada y una activa

        var sessions = await service.ListActiveSessionsAsync(userId, CancellationToken.None);

        var session = Assert.Single(sessions);                          // una sesión, no dos
        Assert.Equal(SidOf(issued.AccessToken), session.SessionId);
        Assert.Equal(started, session.StartedAt);                       // cuándo empezó: el del inicio
        Assert.True(session.LastActiveAt > started);                    // la última actividad: la de la renovación
        Assert.Equal("Chrome/120 (Windows)", session.UserAgent);
        Assert.Equal("203.0.113.0", session.IpAddress);
    }

    [Fact]
    public async Task ListActive_OnlyTheAccountAsked_NeverAnotherOne()
    {
        var (db, service) = CreateSut();
        var (mine, tenantA) = await SeedAccountAsync(db);
        var (theirs, tenantB) = await SeedAccountAsync(db);
        var mySession = await service.IssueTokensAsync(mine, tenantA, TenantRole.Owner, CancellationToken.None);
        await service.IssueTokensAsync(theirs, tenantB, TenantRole.Owner, CancellationToken.None);

        var sessions = await service.ListActiveSessionsAsync(mine, CancellationToken.None);

        Assert.Equal(SidOf(mySession.AccessToken), Assert.Single(sessions).SessionId);
    }

    [Fact]
    public async Task ListActive_DoesNotShowClosedOrExpiredSessions()
    {
        var (db, service) = CreateSut();
        var (userId, tenantId) = await SeedAccountAsync(db);
        var open = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var closed = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        await service.RevokeRefreshTokenAsync(closed.RefreshToken, CancellationToken.None);
        db.RefreshTokens.Add(new RefreshToken(userId, tenantId, Guid.NewGuid(), "hash-caducada", DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync(CancellationToken.None);

        var sessions = await service.ListActiveSessionsAsync(userId, CancellationToken.None);

        Assert.Equal(SidOf(open.AccessToken), Assert.Single(sessions).SessionId);
    }

    [Fact]
    public async Task ListActive_ShowsTheAccountsSessionsInEveryCompany_MostRecentFirst()
    {
        var (_, service) = CreateSut();
        var userId = Guid.NewGuid();
        var first = await service.IssueTokensAsync(userId, Guid.NewGuid(), TenantRole.Owner, CancellationToken.None);
        await Task.Delay(30);
        var second = await service.IssueTokensAsync(userId, Guid.NewGuid(), TenantRole.Technician, CancellationToken.None);

        var sessions = await service.ListActiveSessionsAsync(userId, CancellationToken.None);

        Assert.Equal([SidOf(second.AccessToken), SidOf(first.AccessToken)], sessions.Select(s => s.SessionId));
    }

    [Fact]
    public async Task ListActive_WithNoSessions_IsEmpty_AndWritesNothing()
    {
        var (db, service) = CreateSut();

        Assert.Empty(await service.ListActiveSessionsAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListActive_AnOldSessionWithoutDeviceData_ShowsNullBrowserAndNetwork()
    {
        var (db, service) = CreateSut(client: new FakeClientInfo());
        var (userId, tenantId) = await SeedAccountAsync(db);
        await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var session = Assert.Single(await service.ListActiveSessionsAsync(userId, CancellationToken.None));

        Assert.Null(session.UserAgent);
        Assert.Null(session.IpAddress);
    }

    [Fact]
    public async Task ListActive_ARenewalRaceThatLeavesTwoActiveRowsOfOneSession_ShowsItOnce_WithTheMostRecent()
    {
        // Dos renovaciones a la vez con el mismo refresh token podrían dejar dos filas activas de la misma sesión. La lista no debe enseñarla dos veces.
        var (db, service) = CreateSut();
        var (userId, tenantId) = await SeedAccountAsync(db);
        var sessionId = Guid.NewGuid();
        var started = DateTime.UtcNow.AddHours(-2);
        db.RefreshTokens.AddRange(
            new RefreshToken(userId, tenantId, sessionId, "h-older", DateTime.UtcNow.AddDays(7), createdAt: DateTime.UtcNow.AddMinutes(-20), sessionStartedAt: started, userAgent: "Chrome/120"),
            new RefreshToken(userId, tenantId, sessionId, "h-newer", DateTime.UtcNow.AddDays(7), createdAt: DateTime.UtcNow.AddMinutes(-2), sessionStartedAt: started, userAgent: "Chrome/120"));
        await db.SaveChangesAsync(CancellationToken.None);

        var session = Assert.Single(await service.ListActiveSessionsAsync(userId, CancellationToken.None));

        Assert.Equal(sessionId, session.SessionId);
        Assert.InRange(session.LastActiveAt, DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow.AddMinutes(-1)); // la más reciente de las dos
    }

    // ===== Cerrar una =====

    [Fact]
    public async Task CloseSession_RevokesOnlyThatSession_TheOtherOfTheSameAccountKeepsWorking()
    {
        var (db, service) = CreateSut();
        var (userId, tenantId) = await SeedAccountAsync(db);
        var laptop = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var phone = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var closed = await service.CloseSessionAsync(userId, SidOf(laptop.AccessToken), isCurrentSession: false, CancellationToken.None);

        Assert.True(closed);
        Assert.False(await service.IsSessionActiveAsync(userId, SidOf(laptop.AccessToken), CancellationToken.None));
        Assert.True(await service.IsSessionActiveAsync(userId, SidOf(phone.AccessToken), CancellationToken.None));
    }

    [Fact]
    public async Task CloseSession_RevokesTheWholeChain_AndRevokesInsteadOfDeleting()
    {
        var (db, service) = CreateSut();
        var (userId, tenantId) = await SeedAccountAsync(db);
        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var renewed = (await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None))!;
        var sid = SidOf(issued.AccessToken);

        await service.CloseSessionAsync(userId, sid, isCurrentSession: true, CancellationToken.None);

        Assert.False(await service.IsSessionActiveAsync(userId, sid, CancellationToken.None));
        Assert.Equal(sid, SidOf(renewed.AccessToken));                                         // el token renovado es de la misma sesión
        Assert.All(await db.RefreshTokens.ToListAsync(), rt => Assert.NotNull(rt.RevokedAt));  // las dos filas, revocadas
        Assert.Equal(2, await db.RefreshTokens.CountAsync());                                  // y siguen ahí (R3 de la spec 013)
    }

    [Fact]
    public async Task CloseSession_OfAnotherAccountsSession_ClosesNothing()
    {
        var (db, service) = CreateSut();
        var (mine, tenantA) = await SeedAccountAsync(db);
        var (theirs, tenantB) = await SeedAccountAsync(db);
        var theirSession = await service.IssueTokensAsync(theirs, tenantB, TenantRole.Owner, CancellationToken.None);
        await service.IssueTokensAsync(mine, tenantA, TenantRole.Owner, CancellationToken.None);

        // Yo, con SU Id de sesión y MI cuenta: el UserId forma parte de la condición.
        var closed = await service.CloseSessionAsync(mine, SidOf(theirSession.AccessToken), isCurrentSession: false, CancellationToken.None);

        Assert.False(closed);
        Assert.True(await service.IsSessionActiveAsync(theirs, SidOf(theirSession.AccessToken), CancellationToken.None));
        Assert.All(await db.RefreshTokens.ToListAsync(), rt => Assert.Null(rt.RevokedAt));
    }

    [Fact]
    public async Task CloseSession_UnknownOrAlreadyClosed_IsFalse_AndWritesNothing()
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var (userId, tenantId) = await SeedAccountAsync(db);
        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var sid = SidOf(issued.AccessToken);
        Assert.True(await service.CloseSessionAsync(userId, sid, isCurrentSession: false, CancellationToken.None)); // control: la primera vez sí
        var revokedAt = (await db.RefreshTokens.SingleAsync()).RevokedAt;
        logger.Entries.Clear();

        Assert.False(await service.CloseSessionAsync(userId, sid, isCurrentSession: false, CancellationToken.None));             // ya cerrada
        Assert.False(await service.CloseSessionAsync(userId, Guid.NewGuid(), isCurrentSession: false, CancellationToken.None));  // inexistente
        Assert.False(await service.CloseSessionAsync(userId, Guid.Empty, isCurrentSession: false, CancellationToken.None));

        Assert.Equal(revokedAt, (await db.RefreshTokens.SingleAsync()).RevokedAt); // no se pisa la fecha de la revocación
        Assert.Empty(logger.Entries);                                              // y no hay evento
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseSession_LogsOneEvent_WithTheUserAndWhetherItWasTheCurrentOne_NeverTheSessionIdNorATokenNorAHash(bool current)
    {
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var (userId, tenantId) = await SeedAccountAsync(db);
        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        var sid = SidOf(issued.AccessToken);
        var hash = (await db.RefreshTokens.SingleAsync()).TokenHash;

        await service.CloseSessionAsync(userId, sid, current, CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.SessionClosedByOwner);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(userId.ToString(), entry.Message);
        Assert.Contains($"era la sesión actual: {current}", entry.Message);
        Assert.DoesNotContain(sid.ToString(), entry.Message);
        Assert.DoesNotContain(issued.RefreshToken, entry.Message);
        Assert.DoesNotContain(hash, entry.Message);
        Assert.DoesNotContain(issued.AccessToken, entry.Message);
    }

    [Fact]
    public async Task CloseSession_AfterwardsTheClosedRefreshTokenIsReuse_NotASilentRejection()
    {
        // Se revoca y no se borra (como las specs 013 y 014): quien use después una copia de ese refresh token dispara la detección de reutilización (1003).
        var logger = new ListLogger<TokenService>();
        var (db, service) = CreateSut(logger);
        var (userId, tenantId) = await SeedAccountAsync(db);
        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        await service.CloseSessionAsync(userId, SidOf(issued.AccessToken), isCurrentSession: false, CancellationToken.None);

        var attempt = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.Null(attempt);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokenReuseDetected);
    }
}

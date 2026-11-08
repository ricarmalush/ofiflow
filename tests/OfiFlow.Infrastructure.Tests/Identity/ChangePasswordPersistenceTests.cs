using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Las dos piezas nuevas de cambiar la contraseña estando dentro (spec 019, bloque 1) contra SQL Server real: cerrar <b>todas las sesiones menos la actual</b>
/// (<see cref="TokenService.RevokeOtherRefreshTokensWithoutSavingAsync"/>) y anular los enlaces de recuperación pendientes (<see cref="PasswordResetService.InvalidatePendingWithoutSavingAsync"/>).
/// Ninguna confirma por su cuenta: eso es lo que hace posible el «todo o nada», y se prueba al final con un conflicto de verdad.
/// </summary>
public class ChangePasswordPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Start = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private const string OldPassword = "Clave-vieja-2026!";
    private const string NewPassword = "Clave-nueva-2026!";

    private static readonly JwtOptions Jwt = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private static IdentityService Identity(ApplicationDbContext db) => new(db, NullLogger<IdentityService>.Instance);

    private static TokenService Tokens(ApplicationDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(Jwt), new FakeClientInfo(), NullLogger<TokenService>.Instance);

    private static Guid SidOf(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(c => c.Type == "sid").Value);

    private async Task<(Guid UserId, string Email)> AccountAsync(string password = OldPassword)
    {
        var id = Guid.NewGuid();
        var email = $"persona-{Guid.NewGuid():N}@example.com";
        await using var db = Open();
        await Identity(db).CreateUserAsync(id, email, password, CancellationToken.None);
        // Una empresa y su pertenencia: renovar una sesión relee el rol de la persona en su empresa (sin pertenencia, la renovación se rechaza).
        var tenant = Tenant.Create("Empresa de pruebas");
        db.Tenants.Add(tenant);
        db.TenantUsers.Add(TenantUser.Create(tenant.Id, id, TenantRole.Owner));
        await db.SaveChangesAsync();
        return (id, email);
    }

    /// <summary>Abre una sesión de verdad (un inicio de sesión) y devuelve su Id y su refresh token.</summary>
    private async Task<(Guid SessionId, string Refresh)> SessionAsync(Guid userId)
    {
        await using var db = Open();
        var tenantId = await db.TenantUsers.IgnoreQueryFilters().Where(tu => tu.UserId == userId).Select(tu => tu.TenantId).SingleAsync();
        var issued = await Tokens(db).IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        return (SidOf(issued.AccessToken), issued.RefreshToken);
    }

    private async Task<List<Guid>> ActiveSessionsAsync(Guid userId)
    {
        await using var db = Open();
        return [.. (await Tokens(db).ListActiveSessionsAsync(userId, CancellationToken.None)).Select(s => s.SessionId)];
    }

    private async Task<bool> IsActiveAsync(Guid userId, Guid sessionId)
    {
        await using var db = Open();
        return await Tokens(db).IsSessionActiveAsync(userId, sessionId, CancellationToken.None);
    }

    private async Task<int> RevokeOtherAndSaveAsync(Guid userId, Guid? current)
    {
        await using var db = Open();
        var closed = await Tokens(db).RevokeOtherRefreshTokensWithoutSavingAsync(userId, current, CancellationToken.None);
        await db.SaveChangesAsync();
        return closed;
    }

    // ===== Cerrar las demás sesiones =====

    [Fact]
    public async Task TheIndicatedSessionIsKept_AndTheOthersOfTheAccountAreClosed()
    {
        var (userId, _) = await AccountAsync();
        var first = await SessionAsync(userId);
        var second = await SessionAsync(userId);
        var third = await SessionAsync(userId);

        var closed = await RevokeOtherAndSaveAsync(userId, second.SessionId);

        Assert.Equal(2, closed);
        Assert.Equal([second.SessionId], await ActiveSessionsAsync(userId));
        Assert.True(await IsActiveAsync(userId, second.SessionId));
        // Spec 015: el access token de una sesión cerrada deja de valer en su siguiente petición.
        Assert.False(await IsActiveAsync(userId, first.SessionId));
        Assert.False(await IsActiveAsync(userId, third.SessionId));
    }

    [Fact]
    public async Task TheKeptSession_KeepsWorking_ItsRefreshTokenStillRotates()
    {
        var (userId, _) = await AccountAsync();
        await SessionAsync(userId);
        var kept = await SessionAsync(userId);

        await RevokeOtherAndSaveAsync(userId, kept.SessionId);

        await using var db = Open();
        var rotated = await Tokens(db).RotateRefreshTokenAsync(kept.Refresh, CancellationToken.None);
        Assert.NotNull(rotated);
        Assert.Equal(kept.SessionId, SidOf(rotated!.AccessToken)); // la misma sesión
    }

    [Fact]
    public async Task WithoutTheIdOfTheCurrentSession_ThereIsNothingToKeep_SoAllAreClosed_FailClosed()
    {
        var (userId, _) = await AccountAsync();
        await SessionAsync(userId);
        await SessionAsync(userId);
        await SessionAsync(userId);

        var closed = await RevokeOtherAndSaveAsync(userId, null);

        Assert.Equal(3, closed);
        Assert.Empty(await ActiveSessionsAsync(userId));
    }

    [Fact]
    public async Task ClosingTheOthers_DoesNotConfirmByItself()
    {
        // La confirmación es del caso de uso: si cerrar confirmara, un fallo después dejaría las sesiones cerradas con la contraseña vieja.
        var (userId, _) = await AccountAsync();
        var first = await SessionAsync(userId);
        var second = await SessionAsync(userId);

        await using (var db = Open())
        {
            var closed = await Tokens(db).RevokeOtherRefreshTokensWithoutSavingAsync(userId, first.SessionId, CancellationToken.None);
            Assert.Equal(1, closed);
            // sin SaveChangesAsync
        }

        Assert.Equal(2, (await ActiveSessionsAsync(userId)).Count);
        Assert.True(await IsActiveAsync(userId, second.SessionId));
    }

    [Fact]
    public async Task OnlyTheSessionsOfThatAccountAreClosed_NeverThoseOfAnotherAccount()
    {
        var (aId, _) = await AccountAsync();
        var (bId, _) = await AccountAsync();
        var a1 = await SessionAsync(aId);
        await SessionAsync(aId);
        var b1 = await SessionAsync(bId);
        var b2 = await SessionAsync(bId);

        await RevokeOtherAndSaveAsync(aId, a1.SessionId);

        Assert.Equal([a1.SessionId], await ActiveSessionsAsync(aId));
        Assert.Equal(2, (await ActiveSessionsAsync(bId)).Count); // las de B, intactas
        Assert.True(await IsActiveAsync(bId, b1.SessionId));
        Assert.True(await IsActiveAsync(bId, b2.SessionId));
    }

    [Fact]
    public async Task TheIdOfAnotherAccountsSession_IsNoKeyToKeepAnything_AllOfThisAccountAreClosed()
    {
        // Pasar como «actual» el Id de una sesión AJENA no conserva nada de esta cuenta (y no toca la ajena): la condición lleva el UserId.
        var (aId, _) = await AccountAsync();
        var (bId, _) = await AccountAsync();
        await SessionAsync(aId);
        var b1 = await SessionAsync(bId);

        var closed = await RevokeOtherAndSaveAsync(aId, b1.SessionId);

        Assert.Equal(1, closed);
        Assert.Empty(await ActiveSessionsAsync(aId));
        Assert.True(await IsActiveAsync(bId, b1.SessionId));
    }

    [Fact]
    public async Task ARenewedSession_IsKeptWhole_ItsRevokedAndItsActiveRowAreBothUntouched()
    {
        // Renovar crea una fila nueva con el mismo SessionId y revoca la anterior: la sesión conservada incluye las dos filas.
        var (userId, _) = await AccountAsync();
        var kept = await SessionAsync(userId);
        await SessionAsync(userId);
        await using (var db = Open())
        {
            Assert.NotNull(await Tokens(db).RotateRefreshTokenAsync(kept.Refresh, CancellationToken.None));
        }

        await RevokeOtherAndSaveAsync(userId, kept.SessionId);

        await using var check = Open();
        var rows = await check.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId && t.SessionId == kept.SessionId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.RevokedAt is null); // la fila activa de la sesión conservada sigue activa
        Assert.True(await IsActiveAsync(userId, kept.SessionId));
    }

    [Fact]
    public async Task TheClosedSessions_AreRevokedNotDeleted_SoALaterUseIsDetectedAsReuse()
    {
        var (userId, _) = await AccountAsync();
        var kept = await SessionAsync(userId);
        var other = await SessionAsync(userId);

        await RevokeOtherAndSaveAsync(userId, kept.SessionId);

        await using var db = Open();
        var row = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.UserId == userId && t.SessionId == other.SessionId);
        Assert.NotNull(row.RevokedAt); // la fila sigue ahí, revocada: un refresh token cerrado que alguien use después se detecta como reutilización (1003)
    }

    [Fact]
    public async Task AlreadyClosedSessions_AreNotCountedAndKeepTheirOriginalDate()
    {
        var (userId, _) = await AccountAsync();
        var kept = await SessionAsync(userId);
        var alreadyClosed = await SessionAsync(userId);
        await SessionAsync(userId);
        await using (var db = Open())
        {
            Assert.True(await Tokens(db).CloseSessionAsync(userId, alreadyClosed.SessionId, isCurrentSession: false, CancellationToken.None));
        }

        DateTime? before;
        await using (var db = Open())
        {
            before = (await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.SessionId == alreadyClosed.SessionId)).RevokedAt;
        }

        var closed = await RevokeOtherAndSaveAsync(userId, kept.SessionId);

        Assert.Equal(1, closed); // solo la que estaba activa
        await using var after = Open();
        Assert.Equal(before, (await after.RefreshTokens.AsNoTracking().SingleAsync(t => t.SessionId == alreadyClosed.SessionId)).RevokedAt);
    }

    [Fact]
    public async Task AnAccountWithOnlyTheCurrentSession_ClosesNothing()
    {
        var (userId, _) = await AccountAsync();
        var only = await SessionAsync(userId);

        var closed = await RevokeOtherAndSaveAsync(userId, only.SessionId);

        Assert.Equal(0, closed);
        Assert.Equal([only.SessionId], await ActiveSessionsAsync(userId));
    }

    // ===== Anular los enlaces de recuperación pendientes =====

    private async Task<string> IssuedLinkAsync(string email, FakeTimeProvider time)
    {
        await using var db = Open();
        var issue = await new PasswordResetService(db, time).IssueAsync(email, CancellationToken.None);
        Assert.Equal(PasswordResetIssueOutcome.Issued, issue.Outcome);
        return issue.Token!;
    }

    private async Task<List<PasswordResetToken>> LinksOfAsync(Guid userId)
    {
        await using var db = Open();
        return await db.PasswordResetTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    private async Task InvalidateAndSaveAsync(Guid userId, FakeTimeProvider time)
    {
        await using var db = Open();
        await new PasswordResetService(db, time).InvalidatePendingWithoutSavingAsync(userId, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ThePendingLinksOfTheAccount_AreInvalidated_ButNeverThoseOfAnotherAccount()
    {
        var (aId, aEmail) = await AccountAsync();
        var (bId, bEmail) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        await IssuedLinkAsync(aEmail, time);
        await IssuedLinkAsync(bEmail, time);

        await InvalidateAndSaveAsync(aId, time);

        Assert.All(await LinksOfAsync(aId), row => Assert.NotNull(row.UsedAt));
        Assert.All(await LinksOfAsync(bId), row => Assert.Null(row.UsedAt)); // el de B, intacto
    }

    [Fact]
    public async Task InvalidatingTheLinks_DoesNotConfirmByItself()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        await IssuedLinkAsync(email, time);

        await using (var db = Open())
        {
            await new PasswordResetService(db, time).InvalidatePendingWithoutSavingAsync(userId, CancellationToken.None);
            // sin SaveChangesAsync
        }

        Assert.Null(Assert.Single(await LinksOfAsync(userId)).UsedAt);
    }

    [Fact]
    public async Task ALinkThatWasInvalidated_NoLongerServes_ItIsRejectedAsUsedOrReplaced()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedLinkAsync(email, time);

        await InvalidateAndSaveAsync(userId, time);

        await using var db = Open();
        var result = await new PasswordResetService(db, time).ConsumeAsync(token, CancellationToken.None);
        Assert.Equal(PasswordResetConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("UsedOrReplaced", result.RejectedReason);
    }

    [Fact]
    public async Task InvalidatingKeepsTheDateOfTheLinksAlreadyUsed()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var first = await IssuedLinkAsync(email, time);
        await using (var db = Open())
        {
            await new PasswordResetService(db, time).ConsumeAsync(first, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        time.Advance(TimeSpan.FromMinutes(5));
        await IssuedLinkAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(5));

        await InvalidateAndSaveAsync(userId, time);

        var rows = await LinksOfAsync(userId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Start, rows[0].UsedAt);                      // el primero conserva SU fecha de uso
        Assert.Equal(Start.AddMinutes(10), rows[1].UsedAt);
    }

    [Fact]
    public async Task ALinkThatAlreadyExpired_IsNotPending_AndIsLeftAlone()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        await IssuedLinkAsync(email, time);
        time.Advance(TimeSpan.FromHours(2));

        await InvalidateAndSaveAsync(userId, time);

        Assert.Null(Assert.Single(await LinksOfAsync(userId)).UsedAt); // nada que anular: ya no servía
    }

    [Fact]
    public async Task AnAccountWithoutLinks_IsAnEmptyOperation_NotAnError()
    {
        var (userId, _) = await AccountAsync();

        await InvalidateAndSaveAsync(userId, new FakeTimeProvider(Start));

        Assert.Empty(await LinksOfAsync(userId));
    }

    // ===== Todo o nada, contra la base de datos de verdad =====

    private async Task<bool> PasswordWorksAsync(string email, string password)
    {
        await using var db = Open();
        return await Identity(db).ValidateCredentialsWithoutLockoutAsync(email, password, CancellationToken.None) is not null;
    }

    /// <summary>Hace lo que el caso de uso hace antes de confirmar: cambiar la contraseña, cerrar las demás sesiones y anular los enlaces, en UN mismo contexto y SIN confirmar.</summary>
    private async Task StageTheChangeAsync(ApplicationDbContext db, Guid userId, Guid keptSession, FakeTimeProvider time)
    {
        Assert.NotNull(await Identity(db).SetPasswordAsync(userId, NewPassword, CancellationToken.None));
        await Tokens(db).RevokeOtherRefreshTokensWithoutSavingAsync(userId, keptSession, CancellationToken.None);
        await new PasswordResetService(db, time).InvalidatePendingWithoutSavingAsync(userId, CancellationToken.None);
    }

    [Fact]
    public async Task TheWholeChange_AppliesTogetherInASingleConfirmation()
    {
        var (userId, email) = await AccountAsync();
        var kept = await SessionAsync(userId);
        await SessionAsync(userId);
        var time = new FakeTimeProvider(Start);
        await IssuedLinkAsync(email, time);

        await using (var db = Open())
        {
            await StageTheChangeAsync(db, userId, kept.SessionId, time);

            // Antes de confirmar, nada ha cambiado para nadie más.
            Assert.True(await PasswordWorksAsync(email, OldPassword));
            Assert.Equal(2, (await ActiveSessionsAsync(userId)).Count);
            Assert.Null(Assert.Single(await LinksOfAsync(userId)).UsedAt);

            await db.SaveChangesAsync();
        }

        Assert.True(await PasswordWorksAsync(email, NewPassword));
        Assert.False(await PasswordWorksAsync(email, OldPassword));
        Assert.Equal([kept.SessionId], await ActiveSessionsAsync(userId));
        Assert.NotNull(Assert.Single(await LinksOfAsync(userId)).UsedAt);
    }

    [Fact]
    public async Task WhenAnotherChangeWinsTheRace_TheConfirmationFails_AndNothingChangedForThisOne()
    {
        // Una carrera de verdad: mientras esta petición prepara el cambio, otra canjea el mismo enlace y confirma. El enlace lleva versión de fila: la confirmación de esta falla y,
        // con ella, la contraseña, el cierre de sesiones y la anulación de enlaces: SQL Server lo deshace todo en una sola transacción.
        var (userId, email) = await AccountAsync();
        var kept = await SessionAsync(userId);
        await SessionAsync(userId);
        var time = new FakeTimeProvider(Start);
        var link = await IssuedLinkAsync(email, time);

        await using var loser = Open();
        await StageTheChangeAsync(loser, userId, kept.SessionId, time);

        // La ganadora canjea el enlace (lo marca usado) y confirma ANTES.
        await using (var winner = Open())
        {
            await new PasswordResetService(winner, time).ConsumeAsync(link, CancellationToken.None);
            await winner.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());

        // Nada de lo que esta petición preparaba quedó: ni la contraseña, ni las sesiones cerradas.
        Assert.True(await PasswordWorksAsync(email, OldPassword));
        Assert.False(await PasswordWorksAsync(email, NewPassword));
        Assert.Equal(2, (await ActiveSessionsAsync(userId)).Count);
    }
}

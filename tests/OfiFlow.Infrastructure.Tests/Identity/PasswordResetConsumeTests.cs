using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Canjear un token de restablecimiento y cambiar la contraseña (spec 018, bloque 3) contra SQL Server real. Tres piezas que el caso de uso confirma <b>juntas</b>: canjear el token
/// (<see cref="PasswordResetService.ConsumeAsync"/>), cambiar la contraseña (<see cref="IdentityService.SetPasswordAsync"/>) y cerrar las sesiones
/// (<see cref="TokenService.RevokeAllRefreshTokensWithoutSavingAsync"/>). Ninguna confirma por su cuenta: eso es lo que hace posible el «todo o nada», y se prueba aquí con una carrera de verdad.
/// </summary>
public class PasswordResetConsumeTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Start = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private const string OldPassword = "Clave-vieja-2026!";

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

    private static string UniqueEmail() => $"persona-{Guid.NewGuid():N}@example.com";

    private static IdentityService Identity(ApplicationDbContext db) => new(db, NullLogger<IdentityService>.Instance);

    private static TokenService Tokens(ApplicationDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(Jwt), new FakeClientInfo(), NullLogger<TokenService>.Instance);

    private async Task<(Guid UserId, string Email)> AccountAsync(string password = OldPassword)
    {
        var id = Guid.NewGuid();
        var email = UniqueEmail();
        await using var db = Open();
        await Identity(db).CreateUserAsync(id, email, password, CancellationToken.None);
        await db.SaveChangesAsync();
        return (id, email);
    }

    /// <summary>Crea un token de verdad (por el mismo camino que «he olvidado mi contraseña») y devuelve su valor en claro.</summary>
    private async Task<string> IssuedTokenAsync(string email, FakeTimeProvider time)
    {
        await using var db = Open();
        var issue = await new PasswordResetService(db, time).IssueAsync(email, CancellationToken.None);
        Assert.Equal(PasswordResetIssueOutcome.Issued, issue.Outcome);
        return issue.Token!;
    }

    private async Task<PasswordResetConsumption> ConsumeAndSaveAsync(string token, FakeTimeProvider time)
    {
        await using var db = Open();
        var result = await new PasswordResetService(db, time).ConsumeAsync(token, CancellationToken.None);
        await db.SaveChangesAsync();
        return result;
    }

    private async Task<List<PasswordResetToken>> RowsOfAsync(Guid userId)
    {
        await using var db = Open();
        return await db.PasswordResetTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    private async Task<bool> PasswordWorksAsync(string email, string password)
    {
        await using var db = Open();
        return await Identity(db).ValidateCredentialsWithoutLockoutAsync(email, password, CancellationToken.None) is not null;
    }

    // ===== Canjear: lo que sirve =====

    [Fact]
    public async Task AValidToken_IsConsumed_AndMarkedAsUsedWhenTheCallerSaves()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(10));

        var result = await ConsumeAndSaveAsync(token, time);

        Assert.Equal(PasswordResetConsumeOutcome.Consumed, result.Outcome);
        Assert.Equal(userId, result.UserId);
        Assert.Null(result.RejectedReason);
        var row = Assert.Single(await RowsOfAsync(userId));
        Assert.Equal(Start.AddMinutes(10), row.UsedAt);
    }

    [Fact]
    public async Task Consuming_DoesNotConfirmByItself()
    {
        // La confirmación es del caso de uso: si canjear confirmara, un fallo después dejaría el token gastado sin cambiar la contraseña.
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);

        await using (var db = Open())
        {
            var result = await new PasswordResetService(db, time).ConsumeAsync(token, CancellationToken.None);
            Assert.Equal(PasswordResetConsumeOutcome.Consumed, result.Outcome);
            // sin SaveChangesAsync
        }

        var row = Assert.Single(await RowsOfAsync(userId));
        Assert.Null(row.UsedAt);
        Assert.True(row.IsUsableAt(Start.AddMinutes(1)));
    }

    [Fact]
    public async Task TheTokenOfOneAccount_NeverServesForAnother()
    {
        var (aId, aEmail) = await AccountAsync();
        var (bId, bEmail) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var aToken = await IssuedTokenAsync(aEmail, time);
        await IssuedTokenAsync(bEmail, time);

        var result = await ConsumeAndSaveAsync(aToken, time);

        Assert.Equal(aId, result.UserId);
        Assert.Null(Assert.Single(await RowsOfAsync(bId)).UsedAt); // el de B no se tocó
    }

    [Fact]
    public async Task OtherLiveTokensOfTheAccount_AreInvalidated_ButNeverThoseOfAnotherAccount()
    {
        var (userId, email) = await AccountAsync();
        var (otherId, otherEmail) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        await IssuedTokenAsync(otherEmail, time);

        // Dos tokens vigentes de la misma cuenta (no ocurre por el camino normal, que anula el anterior al pedir otro: se siembra el segundo a mano).
        var token = await IssuedTokenAsync(email, time);
        await using (var db = Open())
        {
            db.PasswordResetTokens.Add(PasswordResetToken.Create(userId, OpaqueToken.Generate().Hash, Start.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        await ConsumeAndSaveAsync(token, time);

        Assert.All(await RowsOfAsync(userId), row => Assert.NotNull(row.UsedAt));
        Assert.All(await RowsOfAsync(otherId), row => Assert.Null(row.UsedAt));
    }

    [Fact]
    public async Task InvalidatingTheOthers_KeepsTheDateOfTheOnesAlreadyUsed()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var first = await IssuedTokenAsync(email, time);
        await ConsumeAndSaveAsync(first, time);
        time.Advance(TimeSpan.FromMinutes(5));
        var second = await IssuedTokenAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(5));

        await ConsumeAndSaveAsync(second, time);

        var rows = await RowsOfAsync(userId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Start, rows[0].UsedAt);                    // el primero conserva SU fecha de uso
        Assert.Equal(Start.AddMinutes(10), rows[1].UsedAt);
    }

    // ===== Canjear: lo que no sirve (con el motivo real, que solo va al registro) =====

    [Fact]
    public async Task AnUnknownToken_IsRejected_AsUnknown_WithoutAnAccount()
    {
        var result = await ConsumeAndSaveAsync(OpaqueToken.Generate().Value, new FakeTimeProvider(Start));

        Assert.Equal(PasswordResetConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("Unknown", result.RejectedReason);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task AnExpiredToken_IsRejected_AtTheExactInstant_AndNotOneTickBefore()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);

        time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        var justBefore = await ConsumeAndSaveAsync(token, time);
        Assert.Equal(PasswordResetConsumeOutcome.Consumed, justBefore.Outcome);

        // Otro token, canjeado en el instante exacto en que caduca (R3: caduca a la hora, ni un tick más).
        time.Advance(TimeSpan.FromHours(1));
        var second = await IssuedTokenAsync(email, time);
        time.Advance(TimeSpan.FromHours(1));
        var atTheInstant = await ConsumeAndSaveAsync(second, time);

        Assert.Equal(PasswordResetConsumeOutcome.Rejected, atTheInstant.Outcome);
        Assert.Equal("Expired", atTheInstant.RejectedReason);
        Assert.Equal(userId, atTheInstant.UserId);
    }

    [Fact]
    public async Task AUsedToken_IsRejected_AsUsedOrReplaced()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);
        await ConsumeAndSaveAsync(token, time);

        var again = await ConsumeAndSaveAsync(token, time);

        Assert.Equal(PasswordResetConsumeOutcome.Rejected, again.Outcome);
        Assert.Equal("UsedOrReplaced", again.RejectedReason);
        Assert.Equal(userId, again.UserId);
    }

    [Fact]
    public async Task ATokenReplacedByANewerRequest_IsRejected_AsUsedOrReplaced()
    {
        var (_, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var first = await IssuedTokenAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await IssuedTokenAsync(email, time);

        var old = await ConsumeAndSaveAsync(first, time);
        var current = await ConsumeAndSaveAsync(second, time);

        Assert.Equal("UsedOrReplaced", old.RejectedReason);
        Assert.Equal(PasswordResetConsumeOutcome.Consumed, current.Outcome);
    }

    [Fact]
    public async Task AUsedAndExpiredToken_IsReportedAsUsed_BecauseThatIsWhatHappenedFirst()
    {
        var (_, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);
        await ConsumeAndSaveAsync(token, time);
        time.Advance(TimeSpan.FromHours(5));

        Assert.Equal("UsedOrReplaced", (await ConsumeAndSaveAsync(token, time)).RejectedReason);
    }

    [Fact]
    public async Task AToken_OfAnAccountThatIsNowAPlatformOperator_IsRejected_AndNotConsumed()
    {
        var (userId, email) = await AccountAsync();
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);
        await using (var db = Open())
        {
            db.PlatformOperators.Add(PlatformOperator.Create(userId, Start));
            await db.SaveChangesAsync();
        }

        var result = await ConsumeAndSaveAsync(token, time);

        Assert.Equal(PasswordResetConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("PlatformOperator", result.RejectedReason);
        Assert.Null(Assert.Single(await RowsOfAsync(userId)).UsedAt);
    }

    // ===== Cambiar la contraseña =====

    [Fact]
    public async Task SettingThePassword_MakesTheNewOneWork_AndTheOldOneStopWorking_OnceSaved()
    {
        var (userId, email) = await AccountAsync();

        await using (var db = Open())
        {
            var returnedEmail = await Identity(db).SetPasswordAsync(userId, "Clave-nueva-2026!", CancellationToken.None);
            Assert.Equal(email, returnedEmail);
            await db.SaveChangesAsync();
        }

        Assert.True(await PasswordWorksAsync(email, "Clave-nueva-2026!"));
        Assert.False(await PasswordWorksAsync(email, OldPassword));
    }

    [Fact]
    public async Task SettingThePassword_DoesNotConfirmByItself()
    {
        var (userId, email) = await AccountAsync();

        await using (var db = Open())
        {
            await Identity(db).SetPasswordAsync(userId, "Clave-nueva-2026!", CancellationToken.None);
            // sin SaveChangesAsync
        }

        Assert.True(await PasswordWorksAsync(email, OldPassword));
        Assert.False(await PasswordWorksAsync(email, "Clave-nueva-2026!"));
    }

    [Fact]
    public async Task TheNewPasswordIsStoredHashed_NotInClear_AndWithADifferentHashEvenIfEqualToTheOldOne()
    {
        var (userId, email) = await AccountAsync();
        string before;
        await using (var db = Open())
        {
            before = (await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == userId)).PasswordHash!;
        }

        await using (var db = Open())
        {
            await Identity(db).SetPasswordAsync(userId, OldPassword, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = Open();
        var after = (await check.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == userId)).PasswordHash!;
        Assert.NotEqual(before, after);              // la sal es nueva
        Assert.DoesNotContain(OldPassword, after);
        Assert.True(await PasswordWorksAsync(email, OldPassword));
    }

    [Fact]
    public async Task SettingThePassword_ResetsTheFailedCounterAndTheLockout_InTheSameSave()
    {
        var (userId, email) = await AccountAsync();
        await using (var db = Open())
        {
            await db.ApplicationUsers.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AccessFailedCount, 5)
                .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(15)));
        }

        await using (var db = Open())
        {
            await Identity(db).SetPasswordAsync(userId, "Clave-nueva-2026!", CancellationToken.None);

            // Todavía sin confirmar: el bloqueo sigue (el reinicio va en la misma confirmación que la contraseña, no antes).
            await using var other = Open();
            var beforeSave = await other.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.Equal(5, beforeSave.AccessFailedCount);
            Assert.NotNull(beforeSave.LockoutEnd);

            await db.SaveChangesAsync();
        }

        await using var check = Open();
        var user = await check.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.True(await PasswordWorksAsync(email, "Clave-nueva-2026!"));
    }

    [Fact]
    public async Task SettingThePassword_OfOneAccount_NeverTouchesAnother()
    {
        var (aId, _) = await AccountAsync();
        var (_, bEmail) = await AccountAsync();

        await using (var db = Open())
        {
            await Identity(db).SetPasswordAsync(aId, "Clave-nueva-2026!", CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.True(await PasswordWorksAsync(bEmail, OldPassword));
    }

    [Fact]
    public async Task SettingThePasswordOfAnAccountThatDoesNotExist_ReturnsNull_AndWritesNothing()
    {
        await using var db = Open();

        var email = await Identity(db).SetPasswordAsync(Guid.NewGuid(), "Clave-nueva-2026!", CancellationToken.None);

        Assert.Null(email);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task SettingThePassword_DoesNotTouchTheSecondFactor()
    {
        // R11, D7: ni lo lee ni lo cambia. El segundo factor sigue exactamente como estaba.
        var (userId, _) = await AccountAsync();
        await using (var db = Open())
        {
            var credential = MfaCredential.CreatePending(userId, "secreto-cifrado");
            credential.Confirm(Start);
            db.MfaCredentials.Add(credential);
            await db.SaveChangesAsync();
        }

        await using (var db = Open())
        {
            await Identity(db).SetPasswordAsync(userId, "Clave-nueva-2026!", CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = Open();
        var stored = await check.MfaCredentials.AsNoTracking().SingleAsync(m => m.UserId == userId);
        Assert.Equal("secreto-cifrado", stored.SecretCipher);
        Assert.Equal(Start, stored.ConfirmedAt);
        Assert.True(stored.IsConfirmed);
    }

    // ===== Cerrar las sesiones sin confirmar =====

    private async Task<(Guid UserId, Guid TenantId)> AccountWithSessionsAsync(int sessions)
    {
        var (userId, _) = await AccountAsync();
        var tenantId = Guid.NewGuid();
        await using var db = Open();
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync();
        for (var i = 0; i < sessions; i++)
        {
            await Tokens(db).IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);
        }

        return (userId, tenantId);
    }

    private async Task<int> ActiveSessionsAsync(Guid userId)
    {
        await using var db = Open();
        return await db.RefreshTokens.AsNoTracking().CountAsync(rt => rt.UserId == userId && rt.RevokedAt == null && rt.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task RevokingWithoutSaving_ClosesNothingUntilTheCallerSaves_ThenClosesAllOfThatAccountOnly()
    {
        var (userId, _) = await AccountWithSessionsAsync(3);
        var (otherId, _) = await AccountWithSessionsAsync(2);

        await using (var db = Open())
        {
            var count = await Tokens(db).RevokeAllRefreshTokensWithoutSavingAsync(userId, CancellationToken.None);
            Assert.Equal(3, count);
            Assert.Equal(3, await ActiveSessionsAsync(userId)); // aún sin confirmar: no se ha cerrado nada

            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await ActiveSessionsAsync(userId));
        Assert.Equal(2, await ActiveSessionsAsync(otherId));
    }

    [Fact]
    public async Task RevokingWithoutSaving_LeavesNoSecurityEvent_BecauseTheCallerLogsItsOwn()
    {
        var (userId, _) = await AccountWithSessionsAsync(1);
        var logger = new ListLogger<TokenService>();

        await using var db = Open();
        var service = new TokenService(db, Microsoft.Extensions.Options.Options.Create(Jwt), new FakeClientInfo(), logger);
        await service.RevokeAllRefreshTokensWithoutSavingAsync(userId, CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    // ===== Todo o nada, con una carrera de verdad (R9, R10) =====

    [Fact]
    public async Task TwoResetsWithTheSameToken_OneWins_AndTheLoserLeavesNothingBehind()
    {
        var (userId, tenantId) = await AccountWithSessionsAsync(2);
        await using var emailDb = Open();
        var email = (await emailDb.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == userId)).Email!;
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);

        // Dos peticiones que leyeron el token como vigente antes de que ninguna confirmara: cada una con su propio contexto, haciendo TODO lo del caso de uso menos confirmar.
        await using var winner = Open();
        await using var loser = Open();
        var winnerConsume = await new PasswordResetService(winner, time).ConsumeAsync(token, CancellationToken.None);
        var loserConsume = await new PasswordResetService(loser, time).ConsumeAsync(token, CancellationToken.None);
        Assert.Equal(PasswordResetConsumeOutcome.Consumed, winnerConsume.Outcome);
        Assert.Equal(PasswordResetConsumeOutcome.Consumed, loserConsume.Outcome);

        await Identity(winner).SetPasswordAsync(userId, "Clave-de-la-ganadora-1!", CancellationToken.None);
        await Tokens(winner).RevokeAllRefreshTokensWithoutSavingAsync(userId, CancellationToken.None);
        await Identity(loser).SetPasswordAsync(userId, "Clave-de-la-perdedora-2!", CancellationToken.None);
        await Tokens(loser).RevokeAllRefreshTokensWithoutSavingAsync(userId, CancellationToken.None);

        await winner.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());

        // R10: gana una; la contraseña es la de la ganadora y la de la perdedora no existe en ningún sitio.
        Assert.True(await PasswordWorksAsync(email, "Clave-de-la-ganadora-1!"));
        Assert.False(await PasswordWorksAsync(email, "Clave-de-la-perdedora-2!"));
        Assert.False(await PasswordWorksAsync(email, OldPassword));
        Assert.Equal(0, await ActiveSessionsAsync(userId));
        _ = tenantId;
    }

    [Fact]
    public async Task IfTheSaveFails_NothingChanges_NotThePassword_NorTheSessions_NorTheToken()
    {
        // Todo o nada: se fuerza el fallo del único SaveChanges (otra petición consumió el token antes) y se comprueba que no se aplicó NADA de lo demás.
        var (userId, tenantId) = await AccountWithSessionsAsync(2);
        await using var emailDb = Open();
        var email = (await emailDb.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == userId)).Email!;
        var time = new FakeTimeProvider(Start);
        var token = await IssuedTokenAsync(email, time);

        await using var attempt = Open();
        await new PasswordResetService(attempt, time).ConsumeAsync(token, CancellationToken.None);
        await Identity(attempt).SetPasswordAsync(userId, "Clave-nueva-2026!", CancellationToken.None);
        await Tokens(attempt).RevokeAllRefreshTokensWithoutSavingAsync(userId, CancellationToken.None);

        // Entre medias, otra petición gasta el token y confirma.
        await ConsumeAndSaveAsync(token, time);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => attempt.SaveChangesAsync());

        Assert.True(await PasswordWorksAsync(email, OldPassword));
        Assert.False(await PasswordWorksAsync(email, "Clave-nueva-2026!"));
        Assert.Equal(2, await ActiveSessionsAsync(userId));
        _ = tenantId;
    }
}

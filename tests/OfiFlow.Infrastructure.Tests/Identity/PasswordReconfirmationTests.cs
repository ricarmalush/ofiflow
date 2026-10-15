using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Volver a pedir la contraseña a quien ya tiene sesión (spec 010 R5-R7), contra SQL Server real. Cuenta para el bloqueo
/// de cuenta igual que el login, pero con una diferencia que importa: un acierto NO pone el contador a cero, porque no es
/// un inicio de sesión (si lo hiciera, un token robado más la contraseña reiniciaría el contador de otro ataque).
/// </summary>
public class PasswordReconfirmationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "Password123!";
    private const string Wrong = "incorrecta";

    private static readonly DateTime Start = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private async Task<Guid> SeedAccountAsync()
    {
        var id = Guid.NewGuid();

        await using var db = Open();
        await new IdentityService(db, NullLogger<IdentityService>.Instance)
            .CreateUserAsync(id, $"{Guid.NewGuid():N}@example.com", Password, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        return id;
    }

    private async Task<bool> ConfirmAsync(Guid id, string password, FakeTimeProvider clock, CountingPasswordChecker checker, ListLogger<IdentityService>? logger = null)
    {
        await using var db = Open();
        var service = new IdentityService(db, logger ?? (Microsoft.Extensions.Logging.ILogger<IdentityService>)NullLogger<IdentityService>.Instance, clock, checker);

        return await service.ConfirmPasswordAsync(id, password, CancellationToken.None);
    }

    private async Task<(int Failed, DateTimeOffset? LockedUntil)> StateAsync(Guid id)
    {
        await using var db = Open();
        var user = await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id);

        return (user.AccessFailedCount, user.LockoutEnd);
    }

    [Fact]
    public async Task TheCorrectPassword_IsConfirmed_AndTheCounterStaysWhereItWas()
    {
        var id = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        Assert.False(await ConfirmAsync(id, Wrong, clock, checker));
        Assert.False(await ConfirmAsync(id, Wrong, clock, checker));
        Assert.Equal(2, (await StateAsync(id)).Failed);

        Assert.True(await ConfirmAsync(id, Password, clock, checker));

        // NO se puso a cero: no es un inicio de sesión. Y el intento reservado para comprobarla se devolvió.
        Assert.Equal(2, (await StateAsync(id)).Failed);
    }

    [Fact]
    public async Task AWrongPassword_IsRejected_AndCounts()
    {
        var id = await SeedAccountAsync();
        var logger = new ListLogger<IdentityService>();

        Assert.False(await ConfirmAsync(id, Wrong, new FakeTimeProvider(Start), new CountingPasswordChecker(), logger));

        Assert.Equal(1, (await StateAsync(id)).Failed);
        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.LoginFailed);
        Assert.Contains("ReauthenticationFailed", entry.Message);
        Assert.DoesNotContain(Wrong, entry.Message);
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAccount_AndThenEvenTheCorrectOneIsRejected()
    {
        var id = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        var logger = new ListLogger<IdentityService>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(await ConfirmAsync(id, Wrong, clock, checker, logger));
        }

        Assert.False(await ConfirmAsync(id, Password, clock, checker, logger)); // bloqueada: ni la correcta

        Assert.NotNull((await StateAsync(id)).LockedUntil);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.AccountLockedOut);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.LoginRejectedWhileLocked);
    }

    [Fact]
    public async Task AfterTheLockout_TheCorrectPasswordIsConfirmedAgain()
    {
        var id = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await ConfirmAsync(id, Wrong, clock, checker);
        }

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.True(await ConfirmAsync(id, Password, clock, checker));
    }

    [Fact]
    public async Task UnknownAccount_LockedAccount_AndWrongPassword_EachDoExactlyOneFullVerification()
    {
        var wrongId = await SeedAccountAsync();
        var lockedId = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await ConfirmAsync(lockedId, Wrong, clock, new CountingPasswordChecker());
        }

        var unknown = new CountingPasswordChecker();
        var locked = new CountingPasswordChecker();
        var wrong = new CountingPasswordChecker();

        Assert.False(await ConfirmAsync(Guid.NewGuid(), Password, clock, unknown));
        Assert.False(await ConfirmAsync(lockedId, Password, clock, locked));
        Assert.False(await ConfirmAsync(wrongId, Wrong, clock, wrong));

        Assert.Equal(1, unknown.TotalVerifications);
        Assert.Equal(1, locked.TotalVerifications);
        Assert.Equal(1, wrong.TotalVerifications);
        Assert.Equal(1, unknown.DummyVerifications);
        Assert.Equal(1, locked.DummyVerifications);
        Assert.Equal(1, wrong.RealVerifications);
    }

    [Fact]
    public async Task FiftySimultaneousWrongConfirmations_PerformExactlyFiveRealVerifications()
    {
        var id = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await ConfirmAsync(id, Wrong, clock, checker);
        })).ToList();

        gate.Set();
        Assert.All(await Task.WhenAll(attempts), Assert.False);

        Assert.Equal(5, checker.RealVerifications);
        Assert.Equal(5, (await StateAsync(id)).Failed);
    }
}

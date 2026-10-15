using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El bloqueo de cuenta (spec 010, bloque 3) contra SQL Server real, porque lo que se prueba es una sentencia
/// atómica de verdad: una base en memoria no puede ejecutarla y, sobre todo, no puede reproducir peticiones que
/// llegan a la vez. Cada caso de "falla" va con su control positivo (la misma preparación, con el resultado bueno).
/// </summary>
public class AccountLockoutTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "Password123!";
    private const string Wrong = "incorrecta";

    private static readonly DateTime Start = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private static IdentityService Service(
        ApplicationDbContext db, FakeTimeProvider clock, CountingPasswordChecker checker, ILogger<IdentityService>? logger = null) =>
        new(db, logger ?? NullLogger<IdentityService>.Instance, clock, checker);

    private async Task<(Guid Id, string Email)> SeedAccountAsync()
    {
        var id = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.com";

        await using var db = Open();
        var created = await new IdentityService(db, NullLogger<IdentityService>.Instance).CreateUserAsync(id, email, Password, CancellationToken.None);
        Assert.True(created.Succeeded);
        await db.SaveChangesAsync(CancellationToken.None);

        return (id, email);
    }

    private async Task<(int Failed, DateTimeOffset? LockedUntil)> StateAsync(Guid id)
    {
        await using var db = Open();
        var user = await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id);

        return (user.AccessFailedCount, user.LockoutEnd);
    }

    /// <summary>Hace <paramref name="count"/> intentos erróneos seguidos con un servicio nuevo cada vez.</summary>
    private async Task FailAsync(string email, int count, FakeTimeProvider clock, CountingPasswordChecker checker)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            await using var db = Open();
            Assert.Null(await Service(db, clock, checker).AuthenticateAsync(email, Wrong, CancellationToken.None));
        }
    }

    private async Task<Guid?> TryAsync(string email, string password, FakeTimeProvider clock, CountingPasswordChecker checker, ILogger<IdentityService>? logger = null)
    {
        await using var db = Open();

        return (await Service(db, clock, checker, logger).AuthenticateAsync(email, password, CancellationToken.None))?.UserId;
    }

    // ===== El camino feliz y el contador =====

    [Fact]
    public async Task ACorrectPassword_Authenticates_AndLeavesTheCounterAtZero()
    {
        var (id, email) = await SeedAccountAsync();
        var checker = new CountingPasswordChecker();

        var result = await TryAsync(email, Password, new FakeTimeProvider(Start), checker);

        Assert.Equal(id, result);
        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(id));
        Assert.Equal(1, checker.RealVerifications);
    }

    [Fact]
    public async Task EachWrongPassword_AddsOneToTheCounter()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        for (var expected = 1; expected <= 4; expected++)
        {
            await FailAsync(email, 1, clock, checker);
            Assert.Equal((expected, (DateTimeOffset?)null), await StateAsync(id)); // aún sin bloquear
        }
    }

    [Fact]
    public async Task TheFifthWrongPassword_LocksTheAccountForFifteenMinutes()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);

        await FailAsync(email, 5, clock, new CountingPasswordChecker());

        var (failed, lockedUntil) = await StateAsync(id);
        Assert.Equal(5, failed);
        Assert.Equal(new DateTimeOffset(Start).AddMinutes(15), lockedUntil);
    }

    [Fact]
    public async Task TheLockout_AppliesEvenWhenTheLockoutEnabledColumnIsFalse_WhichIsTheDefault()
    {
        // IdentityUser trae LockoutEnabled = false por defecto, y las cuentas de OfiFlow se crean así. Si el bloqueo
        // respetara esa columna, ninguna cuenta se bloquearía nunca: el valor por defecto sería inseguro (D7).
        var (id, email) = await SeedAccountAsync();
        await using (var db = Open())
        {
            Assert.False((await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id)).LockoutEnabled);
        }

        await FailAsync(email, 5, new FakeTimeProvider(Start), new CountingPasswordChecker());

        Assert.NotNull((await StateAsync(id)).LockedUntil);
    }

    // ===== Bloqueada: ni la contraseña correcta entra =====

    [Fact]
    public async Task WhileLocked_EvenTheCorrectPasswordIsRejected()
    {
        var (_, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        // Control positivo: antes de los 5 fallos, la contraseña correcta entra.
        Assert.NotNull(await TryAsync(email, Password, clock, checker));

        await FailAsync(email, 5, clock, checker);

        Assert.Null(await TryAsync(email, Password, clock, checker));
    }

    [Fact]
    public async Task AttemptsWhileLocked_DoNotCount_NorExtendTheLockout()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        await FailAsync(email, 5, clock, checker);
        var before = await StateAsync(id);

        clock.Advance(TimeSpan.FromMinutes(7));
        await FailAsync(email, 10, clock, checker);

        Assert.Equal(before, await StateAsync(id)); // ni el contador ni la hora de fin se movieron
    }

    [Fact]
    public async Task AfterFifteenMinutes_TheAccountWorksAgain_AndTheCounterStartsOver()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        await FailAsync(email, 5, clock, checker);

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Null(await TryAsync(email, Password, clock, checker)); // a los 14 minutos sigue bloqueada

        clock.Advance(TimeSpan.FromMinutes(1)); // justo en el minuto 15: el bloqueo ha terminado
        Assert.Equal(id, await TryAsync(email, Password, clock, checker));
        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(id));
    }

    [Fact]
    public async Task AWrongPasswordAfterTheLockoutEnded_StartsCountingFromOne_NotFromFive()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        await FailAsync(email, 5, clock, checker);

        clock.Advance(TimeSpan.FromMinutes(15));
        await FailAsync(email, 1, clock, checker);

        Assert.Equal((1, (DateTimeOffset?)null), await StateAsync(id)); // no se queda bloqueada de nuevo de golpe
    }

    // ===== El contador se pone a cero solo con un acceso completo =====

    [Fact]
    public async Task ASuccessfulLogin_ClearsTheCounter_SoLaterFailuresStartFromZero()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        await FailAsync(email, 3, clock, checker);
        Assert.Equal(3, (await StateAsync(id)).Failed);

        Assert.Equal(id, await TryAsync(email, Password, clock, checker));
        Assert.Equal(0, (await StateAsync(id)).Failed);

        // Sin el reinicio, estos 4 fallos sumarían 7 y la cuenta ya estaría bloqueada.
        await FailAsync(email, 4, clock, checker);
        Assert.Equal((4, (DateTimeOffset?)null), await StateAsync(id));
    }

    [Fact]
    public async Task TheFifthAttempt_IfItIsCorrect_Succeeds_AndLeavesNoLockout()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        await FailAsync(email, 4, clock, checker);

        Assert.Equal(id, await TryAsync(email, Password, clock, checker));
        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(id));
    }

    // ===== Igual por fuera: tres casos, una sola verificación (R19) =====

    [Fact]
    public async Task UnknownEmail_LockedAccount_AndWrongPassword_EachDoExactlyOneFullVerification()
    {
        var (_, wrongEmail) = await SeedAccountAsync();
        var (_, lockedEmail) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        await FailAsync(lockedEmail, 5, clock, new CountingPasswordChecker());

        var unknown = new CountingPasswordChecker();
        var locked = new CountingPasswordChecker();
        var wrong = new CountingPasswordChecker();

        Assert.Null(await TryAsync($"{Guid.NewGuid():N}@example.com", Password, clock, unknown));
        Assert.Null(await TryAsync(lockedEmail, Password, clock, locked));
        Assert.Null(await TryAsync(wrongEmail, Wrong, clock, wrong));

        // Las tres hacen UNA verificación completa: el tiempo no delata cuál era el caso.
        Assert.Equal(1, unknown.TotalVerifications);
        Assert.Equal(1, locked.TotalVerifications);
        Assert.Equal(1, wrong.TotalVerifications);

        // ...pero solo la errónea la hace contra una contraseña real; las otras dos, contra el hash ficticio.
        Assert.Equal(1, unknown.DummyVerifications);
        Assert.Equal(1, locked.DummyVerifications);
        Assert.Equal(1, wrong.RealVerifications);
    }

    [Fact]
    public async Task AnUnknownEmail_ChangesNothing_AndCannotBlockAnything()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Null(await TryAsync($"{Guid.NewGuid():N}@example.com", Wrong, clock, checker));
        }

        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(id));
        Assert.Equal(10, checker.DummyVerifications);
        Assert.Equal(0, checker.RealVerifications);
    }

    [Fact]
    public async Task TheDummyHash_NeverMatches_NoMatterThePassword()
    {
        var (_, email) = await SeedAccountAsync();
        var checker = new CountingPasswordChecker();

        // Ni la contraseña más habitual del mundo entra por el camino del hash ficticio.
        foreach (var candidate in new[] { "password", "123456", "Password123!", "", "dummy", "admin" })
        {
            Assert.False(PasswordHasherChecker.Instance.Verify(null, candidate));
        }

        Assert.Null(await TryAsync($"{Guid.NewGuid():N}@example.com", Password, new FakeTimeProvider(Start), checker));
        Assert.NotNull(await TryAsync(email, Password, new FakeTimeProvider(Start), new CountingPasswordChecker())); // control
    }

    // ===== Cada cuenta lleva su propio contador =====

    [Fact]
    public async Task OneAccountsFailures_NeverAffectAnotherAccount()
    {
        var (anaId, anaEmail) = await SeedAccountAsync();
        var (luisId, luisEmail) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();

        await FailAsync(anaEmail, 5, clock, checker);

        Assert.NotNull((await StateAsync(anaId)).LockedUntil);
        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(luisId));
        Assert.Equal(luisId, await TryAsync(luisEmail, Password, clock, checker)); // Luis entra con normalidad
    }

    // ===== LA CARRERA: peticiones simultáneas (R18) =====

    [Fact]
    public async Task FiftySimultaneousWrongAttempts_PerformExactlyFiveRealVerifications_AndLockTheAccount()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker(); // compartido: cuenta las 50 peticiones

        // Cada petición con su propio contexto, como en la API real; todas arrancan a la vez.
        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            await using var db = Open();
            var service = Service(db, clock, checker);
            gate.Wait();

            return await service.AuthenticateAsync(email, Wrong, CancellationToken.None);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, Assert.Null);

        // El corazón de R18: con "leer, comprobar y sumar", las 50 habrían comprobado la contraseña. Con la
        // reserva atómica, solo las 5 primeras llegan a hacerlo; las otras 45 se rechazan sin comprobar nada.
        Assert.Equal(5, checker.RealVerifications);
        Assert.Equal(45, checker.DummyVerifications);

        var (failed, lockedUntil) = await StateAsync(id);
        Assert.Equal(5, failed);
        Assert.Equal(new DateTimeOffset(Start).AddMinutes(15), lockedUntil);
    }

    [Fact]
    public async Task ASimultaneousBurst_NeverLeavesTheCounterAboveTheThreshold_OrALockWithoutTheCounter()
    {
        // La invariante que hace seguro el diseño: el contador nunca pasa de 5 y el bloqueo existe exactamente
        // cuando llega a 5, en cualquier reparto de las peticiones.
        for (var round = 0; round < 5; round++)
        {
            var (id, email) = await SeedAccountAsync();
            var clock = new FakeTimeProvider(Start);
            var checker = new CountingPasswordChecker();

            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => TryAsync(email, Wrong, clock, checker))));

            var (failed, lockedUntil) = await StateAsync(id);
            Assert.Equal(5, failed);
            Assert.NotNull(lockedUntil);
            Assert.Equal(5, checker.RealVerifications);
        }
    }

    // ===== Eventos de seguridad, sin datos personales =====

    [Fact]
    public async Task TheEvents_AreLoggedOncePerFact_AndContainNoEmailNorPassword()
    {
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        var logger = new ListLogger<IdentityService>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await TryAsync(email, Wrong, clock, checker, logger);
        }

        await TryAsync(email, Password, clock, checker, logger); // bloqueada, aunque la contraseña sea correcta
        await TryAsync("desconocido@example.com", Wrong, clock, checker, logger);

        Assert.Equal(5 + 1, logger.Entries.Count(e => e.EventId.Id == SecurityEventIds.LoginFailed)); // 5 erróneas + 1 desconocido
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.AccountLockedOut);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.LoginRejectedWhileLocked);

        var locked = logger.Entries.Single(e => e.EventId.Id == SecurityEventIds.AccountLockedOut);
        Assert.Equal(LogLevel.Warning, locked.Level);
        Assert.Contains(id.ToString(), locked.Message);
        Assert.Contains("15 minutos", locked.Message);

        // Ningún evento lleva el email ni las contraseñas (ADR-009 R5).
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(email, entry.Message);
            Assert.DoesNotContain("desconocido@example.com", entry.Message);
            Assert.DoesNotContain(Password, entry.Message);
            Assert.DoesNotContain(Wrong, entry.Message);
        });
    }

    [Fact]
    public async Task TheFifthAttempt_IfItIsCorrect_LogsNoLockoutEvent()
    {
        var (_, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        var logger = new ListLogger<IdentityService>();
        await FailAsync(email, 4, clock, checker);

        await TryAsync(email, Password, clock, checker, logger);

        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.AccountLockedOut);
    }

    [Fact]
    public async Task ValidateCredentialsWithoutLockout_NeitherCountsNorBlocks()
    {
        // El método "sin bloqueo" existe y es útil, pero precisamente por eso no cuenta nada (y un test de
        // arquitectura impide usarlo fuera de IdentityService).
        var (id, email) = await SeedAccountAsync();

        await using (var db = Open())
        {
            var service = new IdentityService(db, NullLogger<IdentityService>.Instance);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                Assert.Null(await service.ValidateCredentialsWithoutLockoutAsync(email, Wrong, CancellationToken.None));
            }

            Assert.Equal(id, await service.ValidateCredentialsWithoutLockoutAsync(email, Password, CancellationToken.None));
        }

        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(id));
    }

    // ===== Las piezas por separado: reservar, devolver, limpiar =====

    private AccountLockout Lockout(ApplicationDbContext db, FakeTimeProvider clock) => new(db, clock);

    [Fact]
    public async Task Reserve_ForAnUnknownAccount_ReservesNothing()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = Open();

        var reservation = await Lockout(db, clock).TryReserveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(reservation.Reserved);
    }

    [Fact]
    public async Task Reserve_ReportsTheNewCounter_AndFlagsOnlyTheAttemptThatLocks()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);

        var reservations = new List<AttemptReservation>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await using var db = Open();
            reservations.Add(await Lockout(db, clock).TryReserveAsync(id, CancellationToken.None));
        }

        Assert.Equal([true, true, true, true, true, false], reservations.Select(r => r.Reserved));
        Assert.Equal([1, 2, 3, 4, 5], reservations.Take(5).Select(r => r.FailedCount));
        Assert.Equal([false, false, false, false, true, false], reservations.Select(r => r.LockApplied)); // solo la quinta bloquea
    }

    [Fact]
    public async Task Release_GivesBackOneAttempt_AndNeverGoesBelowZero()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);

        await using (var db = Open())
        {
            await Lockout(db, clock).ReleaseAsync(id, CancellationToken.None); // con 0 no baja de 0
        }

        Assert.Equal(0, (await StateAsync(id)).Failed);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = Open();
            await Lockout(db, clock).TryReserveAsync(id, CancellationToken.None);
        }

        await using (var db = Open())
        {
            await Lockout(db, clock).ReleaseAsync(id, CancellationToken.None);
        }

        Assert.Equal((2, (DateTimeOffset?)null), await StateAsync(id));
    }

    [Fact]
    public async Task Release_BelowTheThreshold_LiftsTheLockoutThatTheReservationItselfCaused()
    {
        // Con 4 fallos, el quinto intento reserva, llega a 5 y bloquea ANTES de saber si la contraseña era buena. Si era
        // buena (cuenta con segundo factor), devolver la reserva debe levantar ese bloqueo: no fue un fallo.
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var db = Open();
            await Lockout(db, clock).TryReserveAsync(id, CancellationToken.None);
        }

        Assert.NotNull((await StateAsync(id)).LockedUntil); // la quinta reserva bloqueó

        await using (var db = Open())
        {
            await Lockout(db, clock).ReleaseAsync(id, CancellationToken.None);
        }

        Assert.Equal((4, (DateTimeOffset?)null), await StateAsync(id));
    }

    [Fact]
    public async Task Release_WhileTheCounterIsStillAtTheThresholdOrAbove_KeepsARealLockout()
    {
        // Un bloqueo con 5 fallos REALES no se toca: tras devolver un intento, el contador sigue en el umbral o por encima.
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var lockedUntil = new DateTimeOffset(Start).AddMinutes(15);

        await using (var db = Open())
        {
            await db.ApplicationUsers.Where(u => u.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, 6).SetProperty(u => u.LockoutEnd, (DateTimeOffset?)lockedUntil));
        }

        await using (var db = Open())
        {
            await Lockout(db, clock).ReleaseAsync(id, CancellationToken.None);
        }

        Assert.Equal((5, (DateTimeOffset?)lockedUntil), await StateAsync(id));
    }

    [Fact]
    public async Task Clear_ResetsTheCounter_AndRemovesTheLockout_OfThatAccountOnly()
    {
        var (anaId, anaEmail) = await SeedAccountAsync();
        var (luisId, luisEmail) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var checker = new CountingPasswordChecker();
        await FailAsync(anaEmail, 5, clock, checker);
        await FailAsync(luisEmail, 2, clock, checker);

        await using (var db = Open())
        {
            await Lockout(db, clock).ClearAsync(anaId, CancellationToken.None);
        }

        Assert.Equal((0, (DateTimeOffset?)null), await StateAsync(anaId));
        Assert.Equal(2, (await StateAsync(luisId)).Failed); // control: la de Luis no se tocó
    }
}

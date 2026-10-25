using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El servicio del segundo factor contra SQL Server real (spec 010, bloque 4). Es donde viven las garantías que los
/// handlers dan por supuestas: el secreto cifrado, un código TOTP que no se acepta dos veces ni siquiera si llega dos
/// veces A LA VEZ, un código de recuperación que se consume una sola vez, y los fallos que cuentan para el bloqueo.
/// </summary>
public partial class MfaServiceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Start = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;
    private readonly AesGcmSecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private MfaService Service(ApplicationDbContext db, FakeTimeProvider clock, ListLogger<MfaService>? logger = null) =>
        new(db, _protector, (Microsoft.Extensions.Logging.ILogger<MfaService>?)logger ?? NullLogger<MfaService>.Instance, clock);

    private async Task<(Guid Id, string Email)> SeedAccountAsync()
    {
        var id = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.com";

        await using var db = Open();
        var created = await new IdentityService(db, NullLogger<IdentityService>.Instance).CreateUserAsync(id, email, "Password123!", CancellationToken.None);
        Assert.True(created.Succeeded);
        await db.SaveChangesAsync(CancellationToken.None);

        return (id, email);
    }

    /// <summary>Pide el secreto (y lo confirma el Handler en su SaveChanges, aquí simulado). Devuelve el secreto en claro.</summary>
    private async Task<byte[]> BeginSetupAsync(Guid userId, FakeTimeProvider clock)
    {
        var secret = TotpSecret.Generate();

        await using var db = Open();
        await Service(db, clock).BeginSetupAsync(userId, secret, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        return secret;
    }

    private static string CodeFor(byte[] secret, FakeTimeProvider clock, int stepOffset = 0) =>
        Totp.Compute(secret, Totp.StepAt(clock.GetUtcNow()) + stepOffset);

    /// <summary>Cuenta con el MFA ya activo y diez códigos de recuperación. Devuelve el secreto y los códigos en claro.</summary>
    private async Task<(Guid Id, byte[] Secret, IReadOnlyList<(string Code, string Hash)> Codes)> SeedEnabledAsync(FakeTimeProvider clock)
    {
        var (id, _) = await SeedAccountAsync();
        var secret = await BeginSetupAsync(id, clock);
        var codes = RecoveryCodes.GenerateBatch(10);

        await using (var db = Open())
        {
            var outcome = await Service(db, clock).ConfirmSetupAsync(id, CodeFor(secret, clock), codes.Select(c => c.Hash).ToList(), CancellationToken.None);
            Assert.Equal(SetupOutcome.Confirmed, outcome);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // El primer código ya está gastado: el siguiente paso de 30 s trae uno nuevo para las pruebas.
        clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 2));

        return (id, secret, codes);
    }

    private async Task<int> FailedCountAsync(Guid id)
    {
        await using var db = Open();
        return (await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id)).AccessFailedCount;
    }

    private async Task<SecondFactorResult> VerifyAsync(Guid id, string code, FakeTimeProvider clock, ListLogger<MfaService>? logger = null)
    {
        await using var db = Open();
        return await Service(db, clock, logger).VerifySecondFactorAsync(id, code, CancellationToken.None);
    }

    // ===== Pedir el secreto =====

    [Fact]
    public async Task BeginSetup_StoresAnEncryptedPendingSecret_AndReturnsTheAccountEmail()
    {
        var (id, email) = await SeedAccountAsync();
        var secret = TotpSecret.Generate();

        await using (var db = Open())
        {
            var returned = await Service(db, new FakeTimeProvider(Start)).BeginSetupAsync(id, secret, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);

            Assert.Equal(email, returned);
        }

        await using var read = Open();
        var stored = await read.MfaCredentials.AsNoTracking().SingleAsync(c => c.UserId == id);
        Assert.False(stored.IsConfirmed);
        Assert.DoesNotContain(Convert.ToBase64String(secret), stored.SecretCipher);
        Assert.Equal(secret, _protector.Unprotect(stored.SecretCipher, id.ToByteArray())); // y se recupera con la clave

        Assert.Equal(new MfaStatus(false, true, 0), await Service(read, new FakeTimeProvider(Start)).GetStatusAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task AskingAgainBeforeConfirming_ReplacesThePendingSecret()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);

        var first = await BeginSetupAsync(id, clock);
        var second = await BeginSetupAsync(id, clock);

        await using var read = Open();
        var stored = await read.MfaCredentials.AsNoTracking().SingleAsync(c => c.UserId == id);
        Assert.Equal(second, _protector.Unprotect(stored.SecretCipher, id.ToByteArray()));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task BeginSetup_OnAnAlreadyConfirmedAccount_RefusesToOverwriteTheSecret()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, _) = await SeedEnabledAsync(clock);

        await using var db = Open();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db, clock).BeginSetupAsync(id, TotpSecret.Generate(), CancellationToken.None));
    }

    // ===== Confirmar =====

    [Fact]
    public async Task ConfirmSetup_WithAValidCode_ActivatesMfa_StoresTenRecoveryHashes_AndRecordsTheStep()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var secret = await BeginSetupAsync(id, clock);
        var codes = RecoveryCodes.GenerateBatch(10);

        await using (var db = Open())
        {
            var outcome = await Service(db, clock).ConfirmSetupAsync(id, CodeFor(secret, clock), codes.Select(c => c.Hash).ToList(), CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);

            Assert.Equal(SetupOutcome.Confirmed, outcome);
        }

        await using var read = Open();
        var credential = await read.MfaCredentials.AsNoTracking().SingleAsync(c => c.UserId == id);
        Assert.True(credential.IsConfirmed);
        Assert.Equal(Totp.StepAt(clock.GetUtcNow()), credential.LastUsedTimeStep); // el primer código queda gastado
        Assert.Equal(new MfaStatus(true, false, 10), await Service(read, clock).GetStatusAsync(id, CancellationToken.None));

        // Lo guardado son hashes, no los códigos en claro.
        var stored = await read.RecoveryCodes.AsNoTracking().Where(c => c.UserId == id).Select(c => c.CodeHash).ToListAsync();
        Assert.Equal(codes.Select(c => c.Hash).Order(), stored.Order());
        Assert.Empty(stored.Intersect(codes.Select(c => c.Code)));
    }

    [Fact]
    public async Task ConfirmSetup_WithAWrongCode_ChangesNothing_AndLogsItWithoutTheCode()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var secret = await BeginSetupAsync(id, clock);
        var logger = new ListLogger<MfaService>();
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        await using (var db = Open())
        {
            var outcome = await Service(db, clock, logger).ConfirmSetupAsync(id, wrong, RecoveryCodes.GenerateBatch(10).Select(c => c.Hash).ToList(), CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);

            Assert.Equal(SetupOutcome.InvalidCode, outcome);
        }

        await using var read = Open();
        Assert.Equal(new MfaStatus(false, true, 0), await Service(read, clock).GetStatusAsync(id, CancellationToken.None));
        Assert.Equal(0, await read.RecoveryCodes.CountAsync(c => c.UserId == id));

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaVerificationFailed);
        Assert.Contains("WrongCode", entry.Message);
        Assert.DoesNotContain(wrong, entry.Message);
    }

    [Fact]
    public async Task ConfirmSetup_WithoutAPendingSecret_OrWithAnAlreadyConfirmedOne_HasNothingToConfirm()
    {
        var clock = new FakeTimeProvider(Start);
        var (noSetup, _) = await SeedAccountAsync();
        var (enabled, secret, _) = await SeedEnabledAsync(clock);

        await using var db = Open();
        var service = Service(db, clock);

        Assert.Equal(SetupOutcome.NoPendingSetup, await service.ConfirmSetupAsync(noSetup, "123456", [], CancellationToken.None));
        Assert.Equal(SetupOutcome.NoPendingSetup, await service.ConfirmSetupAsync(enabled, CodeFor(secret, clock), [], CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmSetup_WithTheCodeOfAnAlreadyUsedStep_IsRejected()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var secret = await BeginSetupAsync(id, clock);
        var step = Totp.StepAt(clock.GetUtcNow());

        // Se simula que ese paso ya se aceptó antes (RFC 6238 §5.2: un paso no se acepta dos veces).
        await using (var setup = Open())
        {
            await setup.MfaCredentials.Where(c => c.UserId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedTimeStep, step));
        }

        await using var db = Open();
        var outcome = await Service(db, clock).ConfirmSetupAsync(id, CodeFor(secret, clock), [], CancellationToken.None);

        Assert.Equal(SetupOutcome.InvalidCode, outcome);
    }

    [Fact]
    public async Task WrongConfirmationCodes_DoNotCountForTheAccountLockout()
    {
        // Decisión de la spec: quien se equivoca al escanear el QR no debe quedarse sin poder entrar. Estos fallos
        // se limitan por IP, no por cuenta.
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var secret = await BeginSetupAsync(id, clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var db = Open();
            Assert.Equal(SetupOutcome.InvalidCode, await Service(db, clock).ConfirmSetupAsync(id, wrong, [], CancellationToken.None));
        }

        Assert.Equal(0, await FailedCountAsync(id));
    }

    [Fact]
    public async Task TwoSimultaneousConfirmations_WithTheSameCode_ActivateItExactlyOnce()
    {
        var (id, _) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);
        var secret = await BeginSetupAsync(id, clock);
        var code = CodeFor(secret, clock);

        async Task<SetupOutcome> ConfirmAsync()
        {
            await using var db = Open();
            var outcome = await Service(db, clock).ConfirmSetupAsync(id, code, RecoveryCodes.GenerateBatch(10).Select(c => c.Hash).ToList(), CancellationToken.None);

            if (outcome == SetupOutcome.Confirmed)
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }

            return outcome;
        }

        var outcomes = await Task.WhenAll(Task.Run(ConfirmAsync), Task.Run(ConfirmAsync));

        // El mismo código dos veces a la vez: solo una confirmación gana (la anotación del paso es atómica).
        Assert.Single(outcomes, outcome => outcome == SetupOutcome.Confirmed);
        await using var read = Open();
        Assert.Equal(10, await read.RecoveryCodes.CountAsync(c => c.UserId == id)); // y solo hay un lote de códigos
    }

    // ===== Comprobar el segundo factor (desactivar, regenerar) =====

    [Fact]
    public async Task ACorrectCode_IsValid_AndGivesTheAttemptBack()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);

        var result = await VerifyAsync(id, CodeFor(secret, clock), clock);

        Assert.True(result.Valid);
        Assert.False(result.UsedRecoveryCode);
        Assert.Equal(0, await FailedCountAsync(id)); // el intento reservado se devolvió
    }

    [Fact]
    public async Task ACorrectCode_DoesNotResetTheCounter_ItOnlyGivesBackItsOwnAttempt()
    {
        // Comprobar un código en una operación con sesión NO es un inicio de sesión: si pusiera el contador a cero,
        // quien tiene un token robado y la contraseña podría reiniciar el contador de otro ataque en curso.
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        Assert.False((await VerifyAsync(id, wrong, clock)).Valid);
        Assert.False((await VerifyAsync(id, wrong, clock)).Valid);
        Assert.Equal(2, await FailedCountAsync(id));

        Assert.True((await VerifyAsync(id, CodeFor(secret, clock), clock)).Valid);

        Assert.Equal(2, await FailedCountAsync(id)); // sigue en 2
    }

    [Fact]
    public async Task TheSameCodeTwice_IsRejectedTheSecondTime_AndTheFailureCounts()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var code = CodeFor(secret, clock);
        var logger = new ListLogger<MfaService>();

        Assert.True((await VerifyAsync(id, code, clock)).Valid);
        var second = await VerifyAsync(id, code, clock, logger);

        Assert.False(second.Valid);
        Assert.Equal(1, await FailedCountAsync(id));
        Assert.Contains("CodeAlreadyUsed", Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaVerificationFailed).Message);
    }

    [Fact]
    public async Task TheNextStep_BringsANewCodeThatIsAccepted()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);

        Assert.True((await VerifyAsync(id, CodeFor(secret, clock), clock)).Valid);

        clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        Assert.True((await VerifyAsync(id, CodeFor(secret, clock), clock)).Valid);
    }

    [Fact]
    public async Task AnEarlierStep_ThanTheLastAccepted_IsRejected_EvenIfItIsInsideTheWindow()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);

        // Se acepta el código del paso SIGUIENTE (móvil adelantado)...
        Assert.True((await VerifyAsync(id, CodeFor(secret, clock, stepOffset: 1), clock)).Valid);

        // ...y los del paso actual y el anterior, aunque estén en la ventana, ya no valen.
        Assert.False((await VerifyAsync(id, CodeFor(secret, clock), clock)).Valid);
        Assert.False((await VerifyAsync(id, CodeFor(secret, clock, stepOffset: -1), clock)).Valid);
    }

    [Fact]
    public async Task AWrongCode_IsRejected_AndAddsOneToTheCounter()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        Assert.False((await VerifyAsync(id, wrong, clock)).Valid);
        Assert.Equal(1, await FailedCountAsync(id));
    }

    [Fact]
    public async Task FiveWrongCodes_LockTheAccount_AndThenEvenTheCorrectCodeIsRejected()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";
        var logger = new ListLogger<MfaService>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False((await VerifyAsync(id, wrong, clock, logger)).Valid);
        }

        // Control positivo: antes de bloquearse, el código correcto habría entrado; ahora no.
        Assert.False((await VerifyAsync(id, CodeFor(secret, clock), clock, logger)).Valid);

        await using var read = Open();
        Assert.NotNull((await read.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id)).LockoutEnd);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.AccountLockedOut);
        Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.LoginRejectedWhileLocked);
        Assert.Contains(logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaVerificationFailed && e.Message.Contains("AccountLocked"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("no es un código")]
    public async Task AMalformedCode_IsRejected_AndCounts(string code)
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, _) = await SeedEnabledAsync(clock);

        Assert.False((await VerifyAsync(id, code, clock)).Valid);
        Assert.Equal(1, await FailedCountAsync(id));
    }

    // ===== Códigos de recuperación =====

    [Fact]
    public async Task ARecoveryCode_IsValidOnce_ConsumedAtomically_AndTheCountDrops()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, codes) = await SeedEnabledAsync(clock);
        var logger = new ListLogger<MfaService>();

        var first = await VerifyAsync(id, codes[0].Code, clock, logger);

        Assert.True(first.Valid);
        Assert.True(first.UsedRecoveryCode);
        Assert.Equal(9, first.RecoveryCodesRemaining);
        Assert.Equal(0, await FailedCountAsync(id)); // acierto: devuelve el intento

        var again = await VerifyAsync(id, codes[0].Code, clock);
        Assert.False(again.Valid); // un código de recuperación vale una sola vez
        Assert.Equal(1, await FailedCountAsync(id));

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RecoveryCodeUsed);
        Assert.Contains("quedan 9", entry.Message);
        Assert.DoesNotContain(codes[0].Code, entry.Message);
    }

    [Fact]
    public async Task ARecoveryCode_CanBeTypedInLowerCaseAndWithoutHyphens()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, codes) = await SeedEnabledAsync(clock);

        var typedByHand = codes[3].Code.Replace("-", "").ToLowerInvariant();

        Assert.True((await VerifyAsync(id, typedByHand, clock)).Valid);
    }

    [Fact]
    public async Task AWrongRecoveryCode_IsRejected_AndCounts()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, _) = await SeedEnabledAsync(clock);
        var logger = new ListLogger<MfaService>();

        var result = await VerifyAsync(id, RecoveryCodes.Generate(), clock, logger);

        Assert.False(result.Valid);
        Assert.Equal(1, await FailedCountAsync(id));
        Assert.Contains("WrongRecoveryCode", Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaVerificationFailed).Message);
    }

    [Fact]
    public async Task ARecoveryCodeOfAnotherAccount_DoesNotWork()
    {
        var clock = new FakeTimeProvider(Start);
        var (ana, _, _) = await SeedEnabledAsync(clock);
        var (_, _, luisCodes) = await SeedEnabledAsync(clock);

        Assert.False((await VerifyAsync(ana, luisCodes[0].Code, clock)).Valid);
        Assert.Equal(1, await FailedCountAsync(ana));
    }

    // ===== LA CARRERA: el mismo código a la vez (riesgo técnico 2 de la spec) =====

    [Fact]
    public async Task FourSimultaneousUses_OfTheSameTotpCode_ProduceExactlyOneValid()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var code = CodeFor(secret, clock);

        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await VerifyAsync(id, code, clock);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(attempts);

        // Sin la anotación atómica del paso, las cuatro darían por bueno el mismo código.
        Assert.Single(results, result => result.Valid);
        Assert.Equal(3, await FailedCountAsync(id)); // las tres que perdieron cuentan como fallo; la que ganó devolvió el suyo
    }

    [Fact]
    public async Task FourSimultaneousUses_OfTheSameRecoveryCode_ProduceExactlyOneValid()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, codes) = await SeedEnabledAsync(clock);

        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await VerifyAsync(id, codes[0].Code, clock);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Valid);

        await using var read = Open();
        Assert.Equal(9, await read.RecoveryCodes.CountAsync(c => c.UserId == id && c.UsedAt == null)); // se consumió uno, no cuatro
    }

    [Fact]
    public async Task ABurstOfTwentyRequests_NeverAcceptsTheSameCodeMoreThanOnce_AndNeverCountsMoreThanTheThreshold()
    {
        // Con 20 a la vez, el bloqueo (5) puede dejar fuera incluso a la buena: lo que no puede pasar es que entren dos.
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var code = CodeFor(secret, clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => VerifyAsync(id, code, clock))));

        Assert.True(results.Count(result => result.Valid) <= 1);
        Assert.InRange(await FailedCountAsync(id), 0, 5);
    }

    // ===== Regenerar y borrar =====

    [Fact]
    public async Task ReplacingTheRecoveryCodes_InvalidatesTheOldOnes_AndTheNewOnesWork()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, oldCodes) = await SeedEnabledAsync(clock);
        var newCodes = RecoveryCodes.GenerateBatch(10);

        await using (var db = Open())
        {
            await Service(db, clock).ReplaceRecoveryCodesAsync(id, newCodes.Select(c => c.Hash).ToList(), CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        Assert.False((await VerifyAsync(id, oldCodes[0].Code, clock)).Valid);
        var fresh = await VerifyAsync(id, newCodes[0].Code, clock);

        Assert.True(fresh.Valid);
        Assert.Equal(9, fresh.RecoveryCodesRemaining);
    }

    [Fact]
    public async Task RemoveAll_DeletesTheSecretTheCodesAndTheChallenges_OfThatAccountOnly()
    {
        var clock = new FakeTimeProvider(Start);
        var (ana, _, _) = await SeedEnabledAsync(clock);
        var (luis, _, _) = await SeedEnabledAsync(clock);

        await using (var seed = Open())
        {
            seed.MfaChallenges.Add(MfaChallenge.Create(ana, Guid.NewGuid().ToString("N"), Start.AddMinutes(5), ChallengeAudience.Tenant));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = Open())
        {
            await Service(db, clock).RemoveAllAsync(ana, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        Assert.Equal(new MfaStatus(false, false, 0), await Service(read, clock).GetStatusAsync(ana, CancellationToken.None));
        Assert.Equal(0, await read.RecoveryCodes.CountAsync(c => c.UserId == ana));
        Assert.Equal(0, await read.MfaChallenges.CountAsync(c => c.UserId == ana));

        // Control: Luis conserva todo.
        Assert.Equal(new MfaStatus(true, false, 10), await Service(read, clock).GetStatusAsync(luis, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAll_IsIdempotent_OnAnAccountWithoutMfa()
    {
        var (id, _) = await SeedAccountAsync();

        await using var db = Open();
        await Service(db, new FakeTimeProvider(Start)).RemoveAllAsync(id, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // ===== Falla cerrado =====

    [Fact]
    public async Task ASecretCopiedToAnotherAccountsRow_IsNotDecrypted_AndFailsClosed()
    {
        var clock = new FakeTimeProvider(Start);
        var (ana, _, _) = await SeedEnabledAsync(clock);
        var (luis, _, _) = await SeedEnabledAsync(clock);

        // Alguien con acceso a la base de datos copia el secreto cifrado de Ana a la fila de Luis.
        await using (var tamper = Open())
        {
            var anaCipher = (await tamper.MfaCredentials.AsNoTracking().SingleAsync(c => c.UserId == ana)).SecretCipher;
            await tamper.MfaCredentials.Where(c => c.UserId == luis).ExecuteUpdateAsync(s => s.SetProperty(c => c.SecretCipher, anaCipher));
        }

        // Atado al Id de la cuenta, en la fila de Luis no se descifra: error criptográfico, nunca "código válido".
        await Assert.ThrowsAnyAsync<CryptographicException>(() => VerifyAsync(luis, "123456", clock));
    }

    [Fact]
    public async Task TheEvents_NeverContainTheSecretTheCodesNorTheEmail()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, codes) = await SeedEnabledAsync(clock);
        var logger = new ListLogger<MfaService>();
        var valid = CodeFor(secret, clock);

        await VerifyAsync(id, valid, clock, logger);
        await VerifyAsync(id, valid, clock, logger); // repetido
        await VerifyAsync(id, codes[0].Code, clock, logger); // recuperación
        await VerifyAsync(id, "000000", clock, logger);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(valid, entry.Message);
            Assert.DoesNotContain(Convert.ToBase64String(secret), entry.Message);
            Assert.DoesNotContain(Base32.Encode(secret), entry.Message);
            Assert.All(codes, code => Assert.DoesNotContain(code.Code, entry.Message));
        });
    }
}

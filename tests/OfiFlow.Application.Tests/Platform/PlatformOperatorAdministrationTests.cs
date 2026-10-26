using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Platform;
using OfiFlow.Application.Tests.Common;

namespace OfiFlow.Application.Tests.Platform;

/// <summary>
/// El alta y la baja de un operador de la plataforma (spec 011 R2, R3, R4 y R17), con dobles. Lo que se vigila aquí es el
/// ORDEN y el «todo o nada»: el primer código se comprueba antes de crear nada, y la cuenta, el segundo factor confirmado,
/// el operador y la auditoría se confirman en un único SaveChanges. El cifrado, los índices y la atomicidad de verdad
/// se prueban contra SQL Server en Infrastructure.Tests.
/// </summary>
public class PlatformOperatorAdministrationTests
{
    private const string Email = "operadora@ofiflow.example";
    private const string Password = "una-clave-larga-123";

    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeIdentityService _identity = new();
    private readonly FakeMfaService _mfa = new();
    private readonly FakePlatformOperators _operators = new();
    private readonly FakePlatformAudit _audit = new();
    private readonly ListLogger<PlatformOperatorAdministration> _logger = new();
    private readonly byte[] _secret = TotpSecret.Generate();

    private PlatformOperatorAdministration Service() =>
        new(_db, _identity, _mfa, _operators, _audit, new FakeTimeProvider(Now), _logger);

    private string CodeAt(long step) => Totp.Compute(_secret, step);

    private string CurrentCode() => CodeAt(Totp.StepAt(new DateTimeOffset(Now)));

    /// <summary>Que no se haya creado, guardado ni anotado nada: ni cuenta, ni segundo factor, ni operador, ni auditoría, ni evento.</summary>
    private void AssertNothingWasCreated()
    {
        Assert.False(_identity.IsEmailRegisteredAsync(Email, CancellationToken.None).Result);
        Assert.Empty(_mfa.Enrollments);
        Assert.Empty(_operators.Added);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformOperatorCreated);
    }

    // ===== Alta =====

    [Fact]
    public async Task Create_WithAValidFirstCode_CreatesEverythingAndCommitsOnce()
    {
        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.Created, result.Outcome);
        Assert.NotEqual(Guid.Empty, result.UserId);

        Assert.True(await _identity.IsEmailRegisteredAsync(Email, CancellationToken.None));
        Assert.Equal(result.UserId, Assert.Single(_operators.Added).UserId);
        Assert.Equal(result.UserId, Assert.Single(_mfa.Enrollments).UserId);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((result.UserId, PlatformAuditActions.OperatorCreated, (Guid?)null), (entry.OperatorUserId, entry.Action, entry.TenantId));
        Assert.Equal(Now, entry.At);

        // R17: la cuenta, el segundo factor, el operador y la auditoría, en UN solo guardado: o todo o nada.
        Assert.Equal(1, _db.SaveCount);
    }

    [Fact]
    public async Task Create_EnrollsTheSecondFactorAlreadyConfirmed_WithTheSecretAndTheStepOfTheFirstCode()
    {
        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        var enrollment = Assert.Single(_mfa.Enrollments);
        Assert.Equal(_secret, enrollment.Secret);
        // El paso del primer código queda anotado como el último aceptado: el mismo código no vale otra vez al iniciar sesión (R4).
        Assert.Equal(Totp.StepAt(new DateTimeOffset(Now)), enrollment.AcceptedStep);
        Assert.Equal(result.UserId, enrollment.UserId);
    }

    [Fact]
    public async Task Create_RecordsTheStepTheCodeMatched_NotTheCurrentOne()
    {
        // Control del reloj del móvil: un código del paso anterior (±30 s) es válido, y lo que se anota es SU paso.
        var step = Totp.StepAt(new DateTimeOffset(Now));

        var result = await Service().CreateAsync(Email, Password, _secret, CodeAt(step - 1), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.Created, result.Outcome);
        Assert.Equal(step - 1, Assert.Single(_mfa.Enrollments).AcceptedStep);
    }

    [Fact]
    public async Task Create_GivesTenRecoveryCodesOnce_AndStoresOnlyTheirHashes()
    {
        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(AuthenticationPolicy.RecoveryCodeCount, result.RecoveryCodesInClear.Count);
        Assert.Equal(result.RecoveryCodesInClear.Count, result.RecoveryCodesInClear.Distinct().Count());

        var hashes = Assert.Single(_mfa.Enrollments).Hashes;
        Assert.Equal(AuthenticationPolicy.RecoveryCodeCount, hashes.Count);
        // Lo guardado no es lo mostrado: en la base de datos solo va el hash.
        Assert.Empty(hashes.Intersect(result.RecoveryCodesInClear));
        Assert.All(result.RecoveryCodesInClear, code => Assert.Contains(RecoveryCodes.Hash(RecoveryCodes.Normalize(code)!), hashes));
    }

    [Theory]
    [InlineData("000000")] // un código con la forma correcta pero que no es el del secreto
    [InlineData("")]
    [InlineData("12345")] // cinco cifras
    [InlineData("abcdef")]
    public async Task Create_WithAnInvalidFirstCode_CreatesNothing(string code)
    {
        // Control: este código no puede coincidir con el real por casualidad.
        Assert.NotEqual(code, CurrentCode());

        var result = await Service().CreateAsync(Email, Password, _secret, code, CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.InvalidCode, result.Outcome);
        AssertNothingWasCreated();
    }

    [Fact]
    public async Task Create_WithTheCodeOfAnotherStepOutsideTheWindow_CreatesNothing()
    {
        // El código es de este secreto, pero de hace cinco minutos: no demuestra que la app esté ahora en manos de la persona.
        var step = Totp.StepAt(new DateTimeOffset(Now));

        var result = await Service().CreateAsync(Email, Password, _secret, CodeAt(step - 10), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.InvalidCode, result.Outcome);
        AssertNothingWasCreated();
    }

    [Fact]
    public async Task Create_WhenTheEmailIsAlreadyRegistered_ChangesNothing()
    {
        // R3: un email de empresa (o de otro operador) no se reutiliza, y no se toca nada.
        _identity.AddExistingAccount(Email);

        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.EmailAlreadyRegistered, result.Outcome);
        Assert.Empty(_mfa.Enrollments);
        Assert.Empty(_operators.Added);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformOperatorCreated);
    }

    [Fact]
    public async Task Create_WhenTheAccountCannotBecomeAnOperator_CommitsNothing()
    {
        // R1: aunque la cuenta pertenezca a una empresa (no debería pasar con una recién creada), no se confirma nada.
        _operators.NextCreation = PlatformOperatorCreation.AccountBelongsToACompany;

        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.NotCreated, result.Outcome);
        Assert.Empty(_mfa.Enrollments);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
    }

    [Fact]
    public async Task Create_WhenTwoCreationsCollideAtSave_ReportsTheEmailAsTaken_AndLogsNothing()
    {
        _db.ExceptionOnSave = new DbUpdateException("índice único");

        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.EmailAlreadyRegistered, result.Outcome);
        Assert.Empty(result.RecoveryCodesInClear);
        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformOperatorCreated);
    }

    [Theory]
    [InlineData("no-es-un-email")]
    [InlineData("")]
    public async Task Create_WithAnInvalidEmail_CreatesNothing(string email)
    {
        var result = await Service().CreateAsync(email, Password, _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.InvalidEmail, result.Outcome);
        Assert.Empty(_mfa.Enrollments);
        Assert.Equal(0, _db.SaveCount);
    }

    [Theory]
    [InlineData(7)] // una menos que el mínimo
    [InlineData(129)] // una más que el máximo: PBKDF2 con una contraseña de tamaño arbitrario sería un DoS barato
    public async Task Create_WithAPasswordOutsideTheRules_CreatesNothing(int length)
    {
        var result = await Service().CreateAsync(Email, new string('a', length), _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.InvalidPassword, result.Outcome);
        AssertNothingWasCreated();
    }

    [Theory]
    [InlineData(8)] // controles positivos: los extremos permitidos sí valen
    [InlineData(128)]
    public async Task Create_WithAPasswordAtTheLimits_Works(int length)
    {
        var result = await Service().CreateAsync(Email, new string('a', length), _secret, CurrentCode(), CancellationToken.None);

        Assert.Equal(OperatorCreationOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task Create_WithASecretOfTheWrongLength_IsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().CreateAsync(Email, Password, new byte[TotpSecret.ByteLength - 1], "123456", CancellationToken.None));
    }

    [Fact]
    public async Task Create_LogsAWarningWithTheIdOnly_NeverTheEmailThePasswordTheSecretOrTheCodes()
    {
        var result = await Service().CreateAsync(Email, Password, _secret, CurrentCode(), CancellationToken.None);

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PlatformOperatorCreated);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(result.UserId.ToString(), entry.Message);

        foreach (var forbidden in new[] { Email, "operadora", Password, Base32.Encode(_secret), CurrentCode() }.Concat(result.RecoveryCodesInClear))
        {
            Assert.DoesNotContain(forbidden, string.Join("\n", _logger.Entries.Select(e => e.Message)));
        }
    }

    // ===== Baja =====

    private async Task<Guid> ExistingOperatorAsync()
    {
        var userId = Guid.NewGuid();
        await _identity.CreateUserAsync(userId, Email, Password, CancellationToken.None);
        _operators.MakeActive(userId);

        return userId;
    }

    [Fact]
    public async Task Disable_AnActiveOperator_DisablesItAuditsItAndCommitsOnce()
    {
        var userId = await ExistingOperatorAsync();

        var outcome = await Service().DisableAsync(Email, CancellationToken.None);

        Assert.Equal(OperatorDisableOutcome.Disabled, outcome);
        Assert.Equal(userId, Assert.Single(_operators.Disabled).UserId);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((userId, PlatformAuditActions.OperatorDisabled, (Guid?)null), (entry.OperatorUserId, entry.Action, entry.TenantId));
        Assert.Equal(1, _db.SaveCount); // la baja y su auditoría, juntas (R17)

        var logged = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PlatformOperatorDisabled);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Contains(userId.ToString(), logged.Message);
        Assert.DoesNotContain(Email, logged.Message);
    }

    [Fact]
    public async Task Disable_AnEmailThatDoesNotExist_ChangesNothing()
    {
        var outcome = await Service().DisableAsync("nadie@ofiflow.example", CancellationToken.None);

        Assert.Equal(OperatorDisableOutcome.NotAnActiveOperator, outcome);
        AssertNothingWasDisabled();
    }

    [Fact]
    public async Task Disable_ACompanyAccount_ChangesNothing()
    {
        // Existe la cuenta, pero es de una empresa: no es un operador. Mismo resultado que un email desconocido.
        await _identity.CreateUserAsync(Guid.NewGuid(), Email, Password, CancellationToken.None);

        var outcome = await Service().DisableAsync(Email, CancellationToken.None);

        Assert.Equal(OperatorDisableOutcome.NotAnActiveOperator, outcome);
        AssertNothingWasDisabled();
    }

    [Fact]
    public async Task Disable_AnOperatorAlreadyDisabled_ChangesNothingTheSecondTime()
    {
        await ExistingOperatorAsync();
        await Service().DisableAsync(Email, CancellationToken.None);
        var savesAfterTheFirst = _db.SaveCount;

        var second = await Service().DisableAsync(Email, CancellationToken.None);

        Assert.Equal(OperatorDisableOutcome.NotAnActiveOperator, second);
        Assert.Single(_operators.Disabled);
        Assert.Single(_audit.Entries); // sin una segunda fila de auditoría
        Assert.Equal(savesAfterTheFirst, _db.SaveCount);
    }

    private void AssertNothingWasDisabled()
    {
        Assert.Empty(_operators.Disabled);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformOperatorDisabled);
    }
}

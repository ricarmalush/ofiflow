using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;
using OfiFlow.Application.Identity.Mfa.Queries.GetMfaStatus;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>Pedir el secreto y confirmarlo: activar el segundo factor (spec 010 R5, R6, R14).</summary>
public class StartAndEnableMfaTests : MfaTestBase
{
    private readonly ListLogger<StartMfaSetupCommandHandler> _startLog = new();
    private readonly ListLogger<EnableMfaCommandHandler> _enableLog = new();

    private Task<MfaSetupDto> StartAsync(string password = Password) =>
        new StartMfaSetupCommandHandler(Db, Me(), Identity, Mfa, _startLog)
            .Handle(new StartMfaSetupCommand(password), CancellationToken.None);

    private Task<RecoveryCodesDto> EnableAsync(string code) =>
        new EnableMfaCommandHandler(Db, Me(), Mfa, Tokens, _enableLog)
            .Handle(new EnableMfaCommand(code), CancellationToken.None);

    // ===== Pedir el secreto =====

    [Fact]
    public async Task StartingTheSetup_ReturnsTheSecretAndTheUri_AndStoresTheSamePendingSecret()
    {
        var setup = await StartAsync();

        Assert.Equal([UserId], Mfa.SetupStarted);
        Assert.Equal(Base32.Encode(Mfa.LastSecret!), setup.Secret); // lo mostrado es lo guardado
        Assert.Equal(32, setup.Secret.Length);
        Assert.StartsWith("otpauth://totp/OfiFlow:ana%40example.com?", setup.OtpAuthUri);
        Assert.Contains($"secret={setup.Secret}", setup.OtpAuthUri);
        Assert.False((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled); // pendiente: aún no cuenta
    }

    [Fact]
    public async Task EverySetup_GetsADifferentSecret()
    {
        var first = await StartAsync();
        var second = await StartAsync();

        Assert.NotEqual(first.Secret, second.Secret);
    }

    [Fact]
    public async Task TheSetup_ChecksThePasswordOfTheCurrentUser()
    {
        await StartAsync();

        Assert.Equal([(UserId, Password)], Identity.PasswordConfirmations);
    }

    [Fact]
    public async Task AWrongPassword_IsRejected_AndNoSecretIsCreated()
    {
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => StartAsync("incorrecta"));

        Assert.Equal(MfaErrors.InvalidPassword, exception.Code);
        Assert.Empty(Mfa.SetupStarted);
    }

    [Fact]
    public async Task TheCorrectPassword_WhenMfaIsAlreadyEnabled_IsRejected()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => StartAsync());

        Assert.Equal(MfaErrors.AlreadyEnabled, exception.Code);
        Assert.Empty(Mfa.SetupStarted);
    }

    [Fact]
    public async Task AWrongPassword_WithMfaAlreadyEnabled_DoesNotRevealThatItIs()
    {
        // Quien solo tiene un token robado y adivina la contraseña mal no debe poder averiguar, por un error
        // distinto, si la cuenta ya tiene el MFA: la contraseña se comprueba antes que el estado.
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => StartAsync("incorrecta"));

        Assert.Equal(MfaErrors.InvalidPassword, exception.Code);
    }

    [Fact]
    public async Task TheSetupEvent_IsLogged_WithoutTheSecretNorTheEmail()
    {
        var setup = await StartAsync();

        var entry = Assert.Single(_startLog.Entries, e => e.EventId.Id == SecurityEventIds.MfaSetupStarted);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.DoesNotContain(setup.Secret, entry.Message);
        Assert.DoesNotContain("ana@example.com", entry.Message);
        Assert.DoesNotContain(Password, entry.Message);
    }

    // ===== Confirmar con el primer código =====

    [Fact]
    public async Task AValidCode_EnablesMfa_AndReturnsTenRecoveryCodesThatMatchTheStoredHashes()
    {
        await StartAsync();

        var result = await EnableAsync(Mfa.ValidCode);

        Assert.True((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
        Assert.Equal(10, result.Codes.Count);
        Assert.Equal(10, result.Codes.Distinct().Count());
        Assert.All(result.Codes, code => Assert.NotNull(RecoveryCodes.Normalize(code)));

        // Lo que se muestra y lo que se guarda son la misma cosa: cada hash guardado es el de un código mostrado,
        // y lo guardado no es el código en claro.
        var shownHashes = result.Codes.Select(code => RecoveryCodes.Hash(RecoveryCodes.Normalize(code)!));
        Assert.Equal(shownHashes.Order(), Mfa.ConfirmedRecoveryHashes!.Order());
        Assert.Empty(Mfa.ConfirmedRecoveryHashes!.Intersect(result.Codes));
    }

    [Fact]
    public async Task EnablingMfa_ClosesEveryOpenSessionOfTheAccount()
    {
        await StartAsync();

        await EnableAsync(Mfa.ValidCode);

        Assert.Equal([UserId], Tokens.UsersWithDeletedTokens); // R14
    }

    [Fact]
    public async Task AWrongCode_DoesNotEnableMfa_NorCloseSessions_NorReturnCodes()
    {
        await StartAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => EnableAsync("000000"));

        Assert.Equal(MfaErrors.InvalidCode, exception.Code);
        Assert.False((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
        Assert.Null(Mfa.ConfirmedRecoveryHashes);
    }

    [Fact]
    public async Task WithoutAPendingSetup_ThereIsNothingToConfirm()
    {
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => EnableAsync(Mfa.ValidCode));

        Assert.Equal(MfaErrors.SetupRequired, exception.Code);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
    }

    [Fact]
    public async Task WhenMfaIsAlreadyEnabled_ConfirmingAgainIsRejected()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => EnableAsync(Mfa.ValidCode));

        Assert.Equal(MfaErrors.AlreadyEnabled, exception.Code);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
    }

    [Fact]
    public async Task TheEnableEvent_IsLogged_WithoutTheCodes()
    {
        await StartAsync();
        var result = await EnableAsync(Mfa.ValidCode);

        var entry = Assert.Single(_enableLog.Entries, e => e.EventId.Id == SecurityEventIds.MfaEnabled);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.All(result.Codes, code => Assert.DoesNotContain(code, entry.Message));
        Assert.DoesNotContain(Mfa.ValidCode, entry.Message);
    }

    // ===== El estado =====

    [Fact]
    public async Task TheStatus_ReflectsTheAccountOfTheCurrentUser_AndCarriesNoSecrets()
    {
        var handler = new GetMfaStatusQueryHandler(Me(), Mfa);

        Assert.Equal(new MfaStatusDto(false, false, 0), await handler.Handle(new GetMfaStatusQuery(), CancellationToken.None));

        await StartAsync();
        Assert.Equal(new MfaStatusDto(false, true, 0), await handler.Handle(new GetMfaStatusQuery(), CancellationToken.None));

        await EnableAsync(Mfa.ValidCode);
        Assert.Equal(new MfaStatusDto(true, false, 10), await handler.Handle(new GetMfaStatusQuery(), CancellationToken.None));

        // El DTO solo tiene tres campos: ninguno puede llevar un secreto ni un código.
        Assert.Equal(["Enabled", "SetupPending", "RecoveryCodesRemaining"], typeof(MfaStatusDto).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task AnotherAccountsMfa_IsNeverVisible()
    {
        // La cuenta sale del token: el estado de otra persona no se puede pedir ni por error.
        Mfa.Set(Guid.NewGuid(), new MfaStatus(true, false, 3));

        var status = await new GetMfaStatusQueryHandler(Me(), Mfa).Handle(new GetMfaStatusQuery(), CancellationToken.None);

        Assert.Equal(new MfaStatusDto(false, false, 0), status);
    }
}

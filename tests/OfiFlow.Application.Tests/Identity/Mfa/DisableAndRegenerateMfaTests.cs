using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>Desactivar el segundo factor y regenerar los códigos de recuperación (spec 010 R7, R14).</summary>
public class DisableAndRegenerateMfaTests : MfaTestBase
{
    private const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private readonly ListLogger<DisableMfaCommandHandler> _disableLog = new();
    private readonly ListLogger<RegenerateRecoveryCodesCommandHandler> _regenerateLog = new();

    private Task DisableAsync(string password, string code) =>
        new DisableMfaCommandHandler(Db, Me(), Identity, Mfa, Tokens, _disableLog)
            .Handle(new DisableMfaCommand(password, code), CancellationToken.None);

    private Task<RecoveryCodesDto> RegenerateAsync(string password, string code) =>
        new RegenerateRecoveryCodesCommandHandler(Db, Me(), Identity, Mfa, _regenerateLog)
            .Handle(new RegenerateRecoveryCodesCommand(password, code), CancellationToken.None);

    // ===== Desactivar =====

    [Fact]
    public async Task Disabling_WithThePasswordAndAValidCode_RemovesEverything_AndClosesTheSessions()
    {
        MfaIsEnabled();

        await DisableAsync(Password, Mfa.ValidCode);

        Assert.Equal([UserId], Mfa.RemovedAll);
        Assert.Equal([UserId], Tokens.UsersWithDeletedTokens); // R14
        Assert.False((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
    }

    [Fact]
    public async Task Disabling_AlsoWorksWithARecoveryCode_ForWhoLostThePhone()
    {
        MfaIsEnabled();
        Mfa.ValidRecoveryCodes.Add(RecoveryCode);

        await DisableAsync(Password, RecoveryCode);

        Assert.Equal([UserId], Mfa.RemovedAll);
    }

    [Fact]
    public async Task Disabling_WithAWrongPassword_ChangesNothing_AndDoesNotEvenCheckTheCode()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync("incorrecta", Mfa.ValidCode));

        Assert.Equal(MfaErrors.InvalidPassword, exception.Code);
        Assert.Empty(Mfa.RemovedAll);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
        Assert.Equal(0, Mfa.SecondFactorChecks); // la contraseña va primero: sin ella, ni se prueba el código
    }

    [Fact]
    public async Task Disabling_WithAWrongCode_ChangesNothing()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync(Password, "000000"));

        Assert.Equal(MfaErrors.InvalidCode, exception.Code);
        Assert.Empty(Mfa.RemovedAll);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
        Assert.True((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
    }

    [Fact]
    public async Task Disabling_WhenThereIsNoMfa_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync(Password, Mfa.ValidCode));

        Assert.Equal(MfaErrors.NotEnabled, exception.Code);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
    }

    [Fact]
    public async Task TheDisableEvent_IsLogged_WithoutPasswordNorCode()
    {
        MfaIsEnabled();
        await DisableAsync(Password, Mfa.ValidCode);

        var entry = Assert.Single(_disableLog.Entries, e => e.EventId.Id == SecurityEventIds.MfaDisabled);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.DoesNotContain(Password, entry.Message);
        Assert.DoesNotContain(Mfa.ValidCode, entry.Message);
    }

    // ===== Regenerar los códigos de recuperación =====

    [Fact]
    public async Task Regenerating_ReplacesTheCodes_WithTenNewOnesWhoseHashesAreTheStoredOnes()
    {
        MfaIsEnabled();

        var result = await RegenerateAsync(Password, Mfa.ValidCode);

        var replaced = Assert.Single(Mfa.ReplacedRecoveryCodes);
        Assert.Equal(UserId, replaced.UserId);
        Assert.Equal(10, result.Codes.Count);
        Assert.Equal(10, result.Codes.Distinct().Count());
        Assert.Equal(
            result.Codes.Select(code => RecoveryCodes.Hash(RecoveryCodes.Normalize(code)!)).Order(),
            replaced.Hashes.Order());
        Assert.Empty(replaced.Hashes.Intersect(result.Codes)); // lo guardado no es el código en claro
    }

    [Fact]
    public async Task Regenerating_DoesNotCloseTheSessions_NorTouchTheSecret()
    {
        MfaIsEnabled();

        await RegenerateAsync(Password, Mfa.ValidCode);

        Assert.Empty(Tokens.UsersWithDeletedTokens);
        Assert.Empty(Mfa.RemovedAll);
        Assert.True((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
    }

    [Fact]
    public async Task EachRegeneration_GivesDifferentCodes()
    {
        MfaIsEnabled();

        var first = await RegenerateAsync(Password, Mfa.ValidCode);
        var second = await RegenerateAsync(Password, Mfa.ValidCode);

        Assert.Empty(first.Codes.Intersect(second.Codes));
    }

    [Fact]
    public async Task Regenerating_WithAWrongPassword_ReplacesNothing()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => RegenerateAsync("incorrecta", Mfa.ValidCode));

        Assert.Equal(MfaErrors.InvalidPassword, exception.Code);
        Assert.Empty(Mfa.ReplacedRecoveryCodes);
        Assert.Equal(0, Mfa.SecondFactorChecks);
    }

    [Fact]
    public async Task Regenerating_WithAWrongCode_ReplacesNothing()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => RegenerateAsync(Password, "000000"));

        Assert.Equal(MfaErrors.InvalidCode, exception.Code);
        Assert.Empty(Mfa.ReplacedRecoveryCodes);
    }

    [Fact]
    public async Task Regenerating_WhenThereIsNoMfa_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => RegenerateAsync(Password, Mfa.ValidCode));

        Assert.Equal(MfaErrors.NotEnabled, exception.Code);
        Assert.Empty(Mfa.ReplacedRecoveryCodes);
    }

    [Fact]
    public async Task TheRegenerationEvent_IsLogged_WithoutTheCodes()
    {
        MfaIsEnabled();
        var result = await RegenerateAsync(Password, Mfa.ValidCode);

        var entry = Assert.Single(_regenerateLog.Entries, e => e.EventId.Id == SecurityEventIds.RecoveryCodesRegenerated);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.All(result.Codes, code => Assert.DoesNotContain(code, entry.Message));
    }
}

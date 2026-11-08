using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>Desactivar el segundo factor y regenerar los códigos de recuperación (spec 010 R7, R14).</summary>
public class DisableAndRegenerateMfaTests : MfaTestBase
{
    private const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private readonly ListLogger<DisableMfaCommandHandler> _disableLog = new();
    private readonly ListLogger<RegenerateRecoveryCodesCommandHandler> _regenerateLog = new();

    // Por defecto la regla exige el factor a Owner y Admin (como en producción); Me() es un Technician, así que estos tests no la notan.
    private Task DisableAsync(string password, string code, FakeCurrentUser? me = null, ISecondFactorRequirement? requirement = null) =>
        new DisableMfaCommandHandler(Db, me ?? Me(), Identity, Mfa, Tokens, requirement ?? new FakeSecondFactorRequirement(TenantRole.Owner, TenantRole.Admin), _disableLog)
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

    // ===== Desactivar con un rol que exige el segundo factor (spec 022 R7) =====

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task WhoseRoleRequiresTheFactor_CannotDisableIt_AndNothingChanges(TenantRole role)
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync(Password, Mfa.ValidCode, Me(role)));

        Assert.Equal(MfaErrors.RequiredForRole, exception.Code);
        Assert.Equal("mfa.required_for_role", exception.Code);
        Assert.Empty(Mfa.RemovedAll);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
        Assert.True((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
    }

    [Fact]
    public async Task TheDenial_IsDecidedBeforeAskingForThePasswordOrTheCode()
    {
        // Con una contraseña ERRÓNEA y un código erróneo, el error sigue siendo el del rol: no se llega a mirar ni una ni otro (no gasta intentos del bloqueo ni da pistas).
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync("incorrecta", "000000", Me(TenantRole.Owner)));

        Assert.Equal(MfaErrors.RequiredForRole, exception.Code);
        Assert.Equal(0, Mfa.SecondFactorChecks);
        Assert.Empty(Identity.PasswordConfirmations);
    }

    [Theory]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public async Task WhoseRoleDoesNotRequireTheFactor_CanDisableIt_AsBefore(TenantRole role)
    {
        MfaIsEnabled();

        await DisableAsync(Password, Mfa.ValidCode, Me(role));

        Assert.Equal([UserId], Mfa.RemovedAll);
    }

    [Fact]
    public async Task WhoWasDemotedFromAdmin_CanDisableIt_TheEffectiveRoleDecidesNotTheTokens()
    {
        // El token (emitido cuando era Admin) sigue diciendo Admin, pero la base de datos ya dice Technician: puede desactivarlo.
        MfaIsEnabled();

        await DisableAsync(Password, Mfa.ValidCode, new FakeCurrentUser(TenantRole.Technician, UserId, TenantId, tokenRole: TenantRole.Admin));

        Assert.Equal([UserId], Mfa.RemovedAll);
    }

    [Fact]
    public async Task WhoWasPromotedToAdmin_CannotDisableIt_EvenIfTheTokenStillSaysTechnician()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => DisableAsync(Password, Mfa.ValidCode, new FakeCurrentUser(TenantRole.Admin, UserId, TenantId, tokenRole: TenantRole.Technician)));

        Assert.Equal(MfaErrors.RequiredForRole, exception.Code);
    }

    [Fact]
    public async Task WithoutAnEffectiveRole_ItIsDenied_FailClosed()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync(Password, Mfa.ValidCode, new FakeCurrentUser(role: null, UserId, TenantId)));

        Assert.Equal(MfaErrors.RequiredForRole, exception.Code);
        Assert.Empty(Mfa.RemovedAll);
    }

    [Fact]
    public async Task TheDecisionComesFromTheRequirementItIsGiven_NotFromACopyOfTheRule()
    {
        // Con una regla que exige el factor al Manager (y a nadie más), un Manager no puede y un Owner sí: el caso de uso pregunta a la interfaz.
        MfaIsEnabled();
        var onlyManagers = new FakeSecondFactorRequirement(TenantRole.Manager);

        var manager = await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync(Password, Mfa.ValidCode, Me(TenantRole.Manager), onlyManagers));
        await DisableAsync(Password, Mfa.ValidCode, Me(TenantRole.Owner), onlyManagers);

        Assert.Equal(MfaErrors.RequiredForRole, manager.Code);
        Assert.Equal([UserId], Mfa.RemovedAll);
    }

    [Fact]
    public async Task TheDenialEvent_IsLogged_WithTheIdsAndTheRole_AndNeverThePasswordNorTheCode()
    {
        MfaIsEnabled();
        var me = Me(TenantRole.Admin);

        await Assert.ThrowsAsync<BusinessRuleException>(() => DisableAsync(Password, Mfa.ValidCode, me));

        var entry = Assert.Single(_disableLog.Entries);
        Assert.Equal(SecurityEventIds.MfaDisableDeniedForRole, entry.EventId.Id);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.Contains(TenantId.ToString(), entry.Message);
        Assert.Contains("Admin", entry.Message);
        Assert.DoesNotContain(Password, entry.Message);
        Assert.DoesNotContain(Mfa.ValidCode, entry.Message);
        Assert.DoesNotContain(SecurityEventIds.MfaDisabled, _disableLog.Entries.Select(e => e.EventId.Id)); // no se dejó el evento de «desactivado»
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

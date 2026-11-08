using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.ChangePassword;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Application.Tests.Identity.Mfa;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Cambiar la contraseña estando dentro (spec 019, bloque 1) con dobles: lo que el caso de uso <b>orquesta</b> y <b>en qué orden</b> (la actual, el código, «distinta», y solo entonces el cambio, todo o nada,
/// en una única confirmación). Que la base de datos de verdad lo haga todo o nada y que el cierre de sesiones y la anulación de enlaces sean solo de esa cuenta lo prueban los tests de Infrastructure.
/// </summary>
public class ChangePasswordCommandHandlerTests : MfaTestBase
{
    private const string NewPassword = "Clave-Nueva-MUY-secreta-2026!";
    private const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS";

    private static readonly Guid SessionId = Guid.NewGuid();

    private readonly FakePasswordResetService _resets = new();
    private readonly FakeEmailQueue _queue = new();
    private readonly ListLogger<ChangePasswordCommandHandler> _log = new();

    private Task ChangeAsync(string current = Password, string next = NewPassword, string? code = null, bool withoutSessionId = false) =>
        new ChangePasswordCommandHandler(
                new FakeCurrentUser(TenantRole.Technician, UserId, TenantId, sessionId: withoutSessionId ? null : SessionId),
                Identity, Mfa, Tokens, _resets, Db, _queue, _log)
            .Handle(new ChangePasswordCommand(current, next, code), CancellationToken.None);

    private void AssertNothingChanged()
    {
        Assert.Empty(Identity.PasswordsSet);
        Assert.Empty(Tokens.OtherSessionsRevokedWithoutSaving);
        Assert.Empty(_resets.UsersWithPendingInvalidated);
        Assert.Empty(_queue.Queued);
        Assert.Equal(0, Db.SaveCount);
    }

    // ===== El camino feliz: todo lo que cambia, y una sola confirmación =====

    [Fact]
    public async Task AValidChange_SetsTheNewPassword_ClosesTheOtherSessions_VoidsTheRecoveryLinks_AndConfirmsOnce()
    {
        await ChangeAsync();

        Assert.Equal([(UserId, Password)], Identity.PasswordConfirmations);   // la actual se pidió para la cuenta del token
        Assert.Equal([(UserId, NewPassword)], Identity.PasswordsSet);
        Assert.Equal([(UserId, (Guid?)SessionId)], Tokens.OtherSessionsRevokedWithoutSaving);
        Assert.Equal([UserId], _resets.UsersWithPendingInvalidated);

        // R5: lo cambia todo junto o nada: UNA sola confirmación. Ni el cambio de contraseña ni el cierre de sesiones ni la anulación de enlaces confirman por su cuenta.
        Assert.Equal(1, Db.SaveCount);
    }

    [Fact]
    public async Task TheCurrentSessionIsTheOneThatIsKept_ItIsHandedToTheRevocation()
    {
        await ChangeAsync();

        // R6: se conserva SOLO la sesión del token. Si el caso de uso pasara otra (o ninguna), cerraría la de quien cambia la contraseña.
        Assert.Equal(SessionId, Assert.Single(Tokens.OtherSessionsRevokedWithoutSaving).CurrentSessionId);
    }

    [Fact]
    public async Task WithoutTheIdOfTheCurrentSession_ThereIsNothingToKeep_SoNoneIsPassed_FailClosed()
    {
        await ChangeAsync(withoutSessionId: true);

        Assert.Null(Assert.Single(Tokens.OtherSessionsRevokedWithoutSaving).CurrentSessionId); // el puerto cierra TODAS cuando no hay «actual»
    }

    [Fact]
    public async Task TheSessionsAreNeverClosedWithTheVariantsThatConfirmOnTheirOwnOrThatCloseEverything()
    {
        await ChangeAsync();

        // La variante que confirma por su cuenta (spec 014) dejaría las sesiones cerradas aunque el cambio de contraseña fallara después; la de «todas sin confirmar» (spec 018) cerraría también la actual.
        Assert.Empty(Tokens.UsersWithAllSessionsRevoked);
        Assert.Empty(Tokens.UsersWithSessionsRevokedWithoutSaving);
        Assert.Empty(Tokens.UsersWithDeletedTokens);
    }

    [Fact]
    public async Task AFixedNoticeIsQueued_ToTheEmailOfTheAccount_AndItNeverCarriesAPassword()
    {
        await ChangeAsync();

        var message = Assert.Single(_queue.Queued);
        Assert.Equal("ana@example.com", message.To.Value);
        var everything = message.Subject + message.TextBody;
        Assert.DoesNotContain(Password, everything);
        Assert.DoesNotContain(NewPassword, everything);
    }

    [Fact]
    public async Task WithAFullQueue_TheChangeStillSucceeds_TheNoticeIsBestEffort()
    {
        _queue.Accepts = false;

        await ChangeAsync();

        Assert.Equal([(UserId, NewPassword)], Identity.PasswordsSet);
        Assert.Equal(1, Db.SaveCount);
        Assert.Single(_log.Entries, e => e.EventId.Id == SecurityEventIds.PasswordChangedFromSession);
    }

    // ===== R1: la contraseña actual =====

    [Fact]
    public async Task AWrongCurrentPassword_ChangesNothing_AndDoesNotEvenCheckTheCode()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(current: "incorrecta", code: Mfa.ValidCode));

        Assert.Equal(IdentityErrors.PasswordCurrentInvalid, exception.Code);
        Assert.Equal("password.current_invalid", exception.Code);
        Assert.Equal(0, Mfa.SecondFactorChecks); // R3: la actual va primero; sin ella, ni se prueba el código (no gasta intentos del bloqueo)
        AssertNothingChanged();
    }

    [Fact]
    public async Task TheCurrentPasswordIsCheckedFirst_BeforeAnythingAboutTheFactorIsRead()
    {
        // Con una actual errónea y SIN código en una cuenta con factor, el error es el de la contraseña: el orden es la seguridad.
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(current: "incorrecta", code: null));

        Assert.Equal(IdentityErrors.PasswordCurrentInvalid, exception.Code);
    }

    // ===== R2: el código, solo con el segundo factor activo =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithTheFactor_AMissingCode_IsRefused_WithoutSpendingACheck(string? code)
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(code: code));

        Assert.Equal(MfaErrors.InvalidCode, exception.Code);
        Assert.Equal(0, Mfa.SecondFactorChecks); // un código ausente no es un intento
        AssertNothingChanged();
    }

    [Fact]
    public async Task WithTheFactor_AWrongCode_ChangesNothing()
    {
        MfaIsEnabled();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(code: "000000"));

        Assert.Equal(MfaErrors.InvalidCode, exception.Code);
        Assert.Equal(1, Mfa.SecondFactorChecks);
        AssertNothingChanged();
    }

    [Fact]
    public async Task WithTheFactor_ARightCode_Changes()
    {
        MfaIsEnabled();

        await ChangeAsync(code: Mfa.ValidCode);

        Assert.Equal(1, Mfa.SecondFactorChecks);
        Assert.Equal([(UserId, NewPassword)], Identity.PasswordsSet);
    }

    [Fact]
    public async Task WithTheFactor_ARecoveryCode_AlsoWorks_ForWhoLostThePhone()
    {
        MfaIsEnabled();
        Mfa.ValidRecoveryCodes.Add(RecoveryCode);

        await ChangeAsync(code: RecoveryCode);

        Assert.Equal([(UserId, NewPassword)], Identity.PasswordsSet);
    }

    [Fact]
    public async Task WithoutTheFactor_TheCodeFieldIsIgnored_AndNeverChecked()
    {
        await ChangeAsync(code: "000000");

        Assert.Equal(0, Mfa.SecondFactorChecks);
        Assert.Equal([(UserId, NewPassword)], Identity.PasswordsSet);
    }

    [Fact]
    public async Task ASetupThatWasRequestedButNotConfirmed_DoesNotCountAsAFactor_SoNoCodeIsAsked()
    {
        SetupIsPending();

        await ChangeAsync();

        Assert.Equal(0, Mfa.SecondFactorChecks);
        Assert.Equal([(UserId, NewPassword)], Identity.PasswordsSet);
    }

    // ===== R3, D5: la nueva, distinta =====

    [Fact]
    public async Task ANewPasswordEqualToTheCurrentOne_IsRefused_AfterProvingTheCurrent()
    {
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(next: Password));

        Assert.Equal(IdentityErrors.PasswordUnchanged, exception.Code);
        Assert.Equal("password.unchanged", exception.Code);
        Assert.Single(Identity.PasswordConfirmations); // se pidió la actual: «distinta» solo se aplica a quien la demostró
        AssertNothingChanged();
    }

    [Fact]
    public async Task TheUnchangedRule_IsAppliedAfterTheCode_NotBefore()
    {
        MfaIsEnabled();

        // Con el factor y un código erróneo, el error es el del código aunque la nueva sea igual a la actual: «distinta» es lo último.
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(next: Password, code: "000000"));

        Assert.Equal(MfaErrors.InvalidCode, exception.Code);
    }

    [Fact]
    public async Task TheUnchangedRule_IsNeverRevealedToWhoDidNotProveTheCurrentPassword()
    {
        // La nueva y la «actual» son la misma cadena, pero la actual es errónea: el error es el de la contraseña, no «es igual» (que confirmaría algo sin demostrar nada).
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync(current: "incorrecta", next: "incorrecta"));

        Assert.Equal(IdentityErrors.PasswordCurrentInvalid, exception.Code);
    }

    [Fact]
    public async Task OnlyAnExactCopyIsRefused_ADifferenceOfCaseIsAnotherPassword()
    {
        await ChangeAsync(next: Password.ToUpperInvariant());

        Assert.Equal([(UserId, Password.ToUpperInvariant())], Identity.PasswordsSet);
    }

    // ===== R5: todo o nada =====

    [Fact]
    public async Task WhenTheConfirmationFails_NothingIsQueued_NothingIsLogged_AndTheErrorIsNeverA500()
    {
        Db.ExceptionOnSave = new DbUpdateException("otro cambio confirmó antes");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync());

        // Como la «actual» que mandó ya no vale (otro cambio la sustituyó), se responde igual que con una actual errónea.
        Assert.Equal(IdentityErrors.PasswordCurrentInvalid, exception.Code);
        Assert.Empty(_queue.Queued);   // el aviso va DESPUÉS de confirmar: un aviso de un cambio que no ocurrió no existe
        Assert.Empty(_log.Entries);    // y tampoco el evento de «contraseña cambiada»
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_ChangesNothing_AndCloseNoSessions()
    {
        Identity.EmailOfTheAccountWhosePasswordIsSet = null;

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => ChangeAsync());

        Assert.Equal(IdentityErrors.PasswordCurrentInvalid, exception.Code);
        Assert.Empty(Tokens.OtherSessionsRevokedWithoutSaving);
        Assert.Empty(_resets.UsersWithPendingInvalidated);
        Assert.Empty(_queue.Queued);
        Assert.Equal(0, Db.SaveCount);
    }

    // ===== R9: no toca el segundo factor =====

    [Fact]
    public async Task TheSecondFactorIsNeverTouched_ItIsOnlyAskedAbout()
    {
        MfaIsEnabled();

        await ChangeAsync(code: Mfa.ValidCode);

        Assert.Empty(Mfa.RemovedAll);
        Assert.True((await Mfa.GetStatusAsync(UserId, CancellationToken.None)).IsEnabled);
    }

    // ===== R14: el evento =====

    [Fact]
    public async Task TheEvent_IsLoggedWithTheIdAndTheClosedSessions_AndNeverASecret()
    {
        MfaIsEnabled();
        Tokens.OtherSessionsClosed = 4;

        await ChangeAsync(code: Mfa.ValidCode);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(SecurityEventIds.PasswordChangedFromSession, entry.EventId.Id);
        Assert.Equal(1023, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.Contains("4", entry.Message);
        Assert.DoesNotContain(Password, entry.Message);
        Assert.DoesNotContain(NewPassword, entry.Message);
        Assert.DoesNotContain(Mfa.ValidCode, entry.Message);
        Assert.DoesNotContain("ana@example.com", entry.Message);
    }

    // ===== El comando =====

    [Fact]
    public void TheCommandNeverPrintsItsSecrets()
    {
        var text = new ChangePasswordCommand(Password, NewPassword, "123456").ToString();

        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain(NewPassword, text);
        Assert.DoesNotContain("123456", text);
    }

    [Fact]
    public void TheCommandRequiresTheChangePasswordPermission_AndIsNotAnonymousNorAllowedWithoutTheSecondFactor()
    {
        var type = typeof(ChangePasswordCommand);

        var permission = Assert.Single(type.GetCustomAttributes(typeof(OfiFlow.Application.Common.Authorization.RequiresPermissionAttribute), inherit: true)
            .Cast<OfiFlow.Application.Common.Authorization.RequiresPermissionAttribute>());
        Assert.Equal(Permission.AccountChangePassword, permission.Permission);
        Assert.False(type.IsDefined(typeof(OfiFlow.Application.Common.Authorization.AllowAnonymousRequestAttribute), inherit: true));
        Assert.False(type.IsDefined(typeof(OfiFlow.Application.Common.Authorization.AllowedWithoutSecondFactorAttribute), inherit: true));
    }
}

/// <summary>Las longitudes se validan ANTES de gastar ninguna comprobación de la contraseña actual (spec 019 R4): un error de campo no cuesta un intento del bloqueo.</summary>
public class ChangePasswordCommandValidatorTests
{
    private static readonly ChangePasswordCommandValidator Validator = new();

    private static ChangePasswordCommand Command(string current = "Actual-2026!", string next = "Nueva-2026-ok!", string? code = null) => new(current, next, code);

    [Fact]
    public void AnOrdinaryCommand_IsValid_WithOrWithoutACode()
    {
        Assert.True(Validator.TestValidate(Command()).IsValid);
        Assert.True(Validator.TestValidate(Command(code: "123456")).IsValid);
    }

    [Theory]
    [InlineData(1)]      // la actual no tiene mínimo: si es corta, simplemente no será la suya
    [InlineData(128)]
    public void TheCurrentPassword_AcceptsFromOneTo128Characters(int length) =>
        Validator.TestValidate(Command(current: new string('a', length))).ShouldNotHaveValidationErrorFor(x => x.CurrentPassword);

    [Theory]
    [InlineData("")]
    public void TheCurrentPassword_CannotBeEmpty(string current) =>
        Validator.TestValidate(Command(current: current)).ShouldHaveValidationErrorFor(x => x.CurrentPassword);

    [Fact]
    public void TheCurrentPassword_HasAnUpperBound_SoNoHugeStringIsHashed() =>
        Validator.TestValidate(Command(current: new string('a', 129))).ShouldHaveValidationErrorFor(x => x.CurrentPassword);

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public void TheNewPassword_AcceptsFromEightTo128Characters(int length) =>
        Validator.TestValidate(Command(next: new string('b', length))).ShouldNotHaveValidationErrorFor(x => x.NewPassword);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(129)]
    public void TheNewPassword_OutsideEightTo128_IsAFieldError(int length) =>
        Validator.TestValidate(Command(next: new string('b', length))).ShouldHaveValidationErrorFor(x => x.NewPassword);

    [Fact]
    public void TheNewPassword_NeedsNoCompositionRules_OnlyTheLength() =>
        Validator.TestValidate(Command(next: "aaaaaaaa")).ShouldNotHaveValidationErrorFor(x => x.NewPassword); // NIST SP 800-63B: ni mayúsculas ni símbolos obligatorios

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void TheCode_HasTheSameBoundAsTheSecondFactor(int length, bool valid)
    {
        var result = Validator.TestValidate(Command(code: new string('1', length)));

        Assert.Equal(valid, result.IsValid);
    }
}

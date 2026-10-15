using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;
using OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>La forma de la entrada se comprueba antes de procesar nada (ADR-009 R3): longitud y alfabeto exactos.</summary>
public class MfaValidatorsTests
{
    private static readonly string TooLongPassword = new('a', PasswordRules.MaxLength + 1);

    // ===== Pedir el secreto =====

    [Fact]
    public void StartSetup_AcceptsANormalPassword_AndRejectsEmptyOrHuge()
    {
        var validator = new StartMfaSetupCommandValidator();

        Assert.True(validator.Validate(new StartMfaSetupCommand("Password123!")).IsValid);
        Assert.False(validator.Validate(new StartMfaSetupCommand("")).IsValid);
        Assert.False(validator.Validate(new StartMfaSetupCommand(TooLongPassword)).IsValid); // no se hashea un texto enorme
        Assert.True(validator.Validate(new StartMfaSetupCommand(new string('a', PasswordRules.MaxLength))).IsValid); // el límite exacto vale
    }

    // ===== Confirmar =====

    [Theory]
    [InlineData("000000")]
    [InlineData("123456")]
    public void Enable_AcceptsExactlySixDigits(string code)
    {
        Assert.True(new EnableMfaCommandValidator().Validate(new EnableMfaCommand(code)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData(" 123456")]
    [InlineData("ABCD-EFGH-JKMN-PQRS")] // un código de recuperación no vale para confirmar
    public void Enable_RejectsAnythingElse_WithTheInvalidCodeError(string code)
    {
        var result = new EnableMfaCommandValidator().Validate(new EnableMfaCommand(code));

        Assert.False(result.IsValid);
        Assert.Equal(MfaErrors.InvalidCode, Assert.Single(result.Errors).ErrorCode);
    }

    // ===== Desactivar y regenerar: un código de la app o uno de recuperación =====

    [Theory]
    [InlineData("123456")]
    [InlineData("ABCD-EFGH-JKMN-PQRS")]
    [InlineData("abcdefghjkmnpqrs")] // sin guiones y en minúsculas
    public void DisableAndRegenerate_AcceptEitherKindOfCode(string code)
    {
        Assert.True(new DisableMfaCommandValidator().Validate(new DisableMfaCommand("Password123!", code)).IsValid);
        Assert.True(new RegenerateRecoveryCodesCommandValidator().Validate(new RegenerateRecoveryCodesCommand("Password123!", code)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("ABCD-EFGH-JKMN-PQRU")] // la U no existe en el alfabeto
    [InlineData("ABCD EFGH JKMN PQRS")]
    [InlineData("no es un código")]
    public void DisableAndRegenerate_RejectAnythingElse(string code)
    {
        var disable = new DisableMfaCommandValidator().Validate(new DisableMfaCommand("Password123!", code));
        var regenerate = new RegenerateRecoveryCodesCommandValidator().Validate(new RegenerateRecoveryCodesCommand("Password123!", code));

        Assert.False(disable.IsValid);
        Assert.False(regenerate.IsValid);
        Assert.Equal(MfaErrors.InvalidCode, Assert.Single(disable.Errors).ErrorCode);
    }

    [Fact]
    public void DisableAndRegenerate_AlsoDemandAPasswordOfReasonableSize()
    {
        Assert.False(new DisableMfaCommandValidator().Validate(new DisableMfaCommand("", "123456")).IsValid);
        Assert.False(new DisableMfaCommandValidator().Validate(new DisableMfaCommand(TooLongPassword, "123456")).IsValid);
        Assert.False(new RegenerateRecoveryCodesCommandValidator().Validate(new RegenerateRecoveryCodesCommand("", "123456")).IsValid);
        Assert.False(new RegenerateRecoveryCodesCommandValidator().Validate(new RegenerateRecoveryCodesCommand(TooLongPassword, "123456")).IsValid);
    }

    [Fact]
    public void TheSecondFactorCodeShape_IsTheUnionOfTheTwoKinds()
    {
        Assert.True(SecondFactorCode.IsWellFormed("123456"));
        Assert.True(SecondFactorCode.IsWellFormed(RecoveryCodes.Generate()));
        Assert.False(SecondFactorCode.IsWellFormed(null));
        Assert.False(SecondFactorCode.IsWellFormed("12345"));
    }
}

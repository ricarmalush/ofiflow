using FluentValidation;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;

public sealed class RegenerateRecoveryCodesCommandValidator : AbstractValidator<RegenerateRecoveryCodesCommand>
{
    public RegenerateRecoveryCodesCommandValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
        RuleFor(x => x.Code).Must(SecondFactorCode.IsWellFormed).WithErrorCode(MfaErrors.InvalidCode);
    }
}

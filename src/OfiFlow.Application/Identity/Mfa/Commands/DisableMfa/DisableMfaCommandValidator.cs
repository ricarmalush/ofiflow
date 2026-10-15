using FluentValidation;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;

public sealed class DisableMfaCommandValidator : AbstractValidator<DisableMfaCommand>
{
    public DisableMfaCommandValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);

        // Un código de la app (6 dígitos) o uno de recuperación: se comprueba la forma antes de procesar nada.
        RuleFor(x => x.Code).Must(SecondFactorCode.IsWellFormed).WithErrorCode(MfaErrors.InvalidCode);
    }
}

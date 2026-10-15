using FluentValidation;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;

public sealed class EnableMfaCommandValidator : AbstractValidator<EnableMfaCommand>
{
    public EnableMfaCommandValidator()
    {
        // Exactamente 6 dígitos, antes de calcular nada (ADR-009 R3).
        RuleFor(x => x.Code).Must(Totp.IsWellFormed).WithErrorCode(MfaErrors.InvalidCode);
    }
}

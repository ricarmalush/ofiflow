using FluentValidation;

namespace OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;

public sealed class StartMfaSetupCommandValidator : AbstractValidator<StartMfaSetupCommand>
{
    public StartMfaSetupCommandValidator()
    {
        // El máximo evita ejecutar PBKDF2 sobre entradas de tamaño arbitrario (ADR-009 R3).
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

using FluentValidation;
using OfiFlow.Application.Common.Validation;

namespace OfiFlow.Application.Identity.Commands.Login;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().ValidEmail();

        // Sin mínimo en el login: una contraseña corta simplemente no coincidirá. El máximo sí,
        // para no ejecutar PBKDF2 sobre entradas de tamaño arbitrario (ADR-009 R3).
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

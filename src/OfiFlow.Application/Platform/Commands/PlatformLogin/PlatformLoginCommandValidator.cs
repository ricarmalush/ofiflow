using FluentValidation;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Application.Identity;

namespace OfiFlow.Application.Platform.Commands.PlatformLogin;

public sealed class PlatformLoginCommandValidator : AbstractValidator<PlatformLoginCommand>
{
    public PlatformLoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().ValidEmail();

        // Igual que el login de empresa: sin mínimo (una contraseña corta simplemente no coincidirá) y con máximo, para no
        // ejecutar PBKDF2 sobre entradas de tamaño arbitrario (ADR-009 R3).
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

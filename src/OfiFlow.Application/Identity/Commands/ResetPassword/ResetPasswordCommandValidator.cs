using FluentValidation;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.ResetPassword;

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        // R7: la forma exacta del token (longitud y alfabeto) se comprueba antes de hashear ni buscar nada: no se procesa texto arbitrario de tamaño ilimitado (ADR-009 R3). Lleva el
        // mismo código que cualquier otro fallo del token (R8): quien lo intenta no aprende nada de por qué no sirve.
        RuleFor(x => x.Token).Must(OpaqueToken.IsWellFormed).WithErrorCode(IdentityErrors.PasswordResetInvalid);

        // Las mismas reglas del registro (R7). El máximo evita ejecutar PBKDF2 sobre entradas enormes: este endpoint es anónimo.
        RuleFor(x => x.NewPassword).NotEmpty().Length(PasswordRules.MinLength, PasswordRules.MaxLength);
    }
}

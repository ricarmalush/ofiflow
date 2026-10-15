using FluentValidation;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.AcceptInvitation;

public sealed class AcceptInvitationCommandValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationCommandValidator()
    {
        // La forma exacta del token (longitud y alfabeto) se comprueba antes de hashear ni buscar nada: no se
        // procesa texto arbitrario de tamaño ilimitado (ADR-009 R3). Usa el mismo código que cualquier otro
        // fallo del token: quien lo intenta no aprende nada de por qué no sirve (spec 009 R7).
        RuleFor(x => x.Token).Must(InvitationToken.IsWellFormed).WithErrorCode(InvitationErrors.Invalid);

        RuleFor(x => x.Name).NotEmpty().MaximumLength(User.NameMaxLength);

        // Mismo mínimo y máximo que el registro (el máximo evita ejecutar PBKDF2 sobre entradas enormes).
        RuleFor(x => x.Password).NotEmpty().Length(PasswordRules.MinLength, PasswordRules.MaxLength);
    }
}

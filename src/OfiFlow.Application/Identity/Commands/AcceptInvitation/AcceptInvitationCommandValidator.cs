using OfiFlow.Application.Common.Security;
using FluentValidation;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.AcceptInvitation;

public sealed class AcceptInvitationCommandValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationCommandValidator(ILegalTerms legalTerms)
    {
        // La forma exacta del token (longitud y alfabeto) se comprueba antes de hashear ni buscar nada: no se
        // procesa texto arbitrario de tamaño ilimitado (ADR-009 R3). Usa el mismo código que cualquier otro
        // fallo del token: quien lo intenta no aprende nada de por qué no sirve (spec 009 R7).
        RuleFor(x => x.Token).Must(OpaqueToken.IsWellFormed).WithErrorCode(InvitationErrors.Invalid);

        RuleFor(x => x.Name).NotEmpty().MaximumLength(User.NameMaxLength);

        // Mismo mínimo y máximo que el registro (el máximo evita ejecutar PBKDF2 sobre entradas enormes).
        RuleFor(x => x.Password).NotEmpty().Length(PasswordRules.MinLength, PasswordRules.MaxLength);

        // D8, R16: sin la versión vigente de las condiciones no hay cuenta. Es un error de CAMPO: depende solo de lo que se envió, nunca del token. Sin versión configurada ninguna es la
        // vigente (fail-closed, como el registro, R8): no nace una cuenta sin condiciones aceptadas.
        RuleFor(x => x.AcceptedTermsVersion).Must(legalTerms.IsCurrent).WithErrorCode(IdentityErrors.TermsNotAccepted);
    }
}

using FluentValidation;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.RequestRegistration;

/// <summary>
/// Solo la forma de lo escrito y que se aceptaron las condiciones vigentes (spec 021 R3). Es lo único que se comprueba <b>sin consultar si el email tiene cuenta</b>, así que un <c>400</c> depende
/// únicamente del texto recibido y nunca delata a una cuenta existente (R1).
/// </summary>
public sealed class RequestRegistrationCommandValidator : AbstractValidator<RequestRegistrationCommand>
{
    public RequestRegistrationCommandValidator(ILegalTerms legalTerms)
    {
        // Las mismas reglas de longitud que el registro actual, con las constantes que usa EF: lo que el validador deja pasar siempre cabe en la columna.
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(User.NameMaxLength);
        RuleFor(x => x.Email).NotEmpty().ValidEmail();

        // R3 y R16: la versión enviada debe ser la vigente. Con la versión sin configurar el registro está cerrado (R8) y lo decide el handler con un 202: aquí no se rechaza, porque la
        // respuesta no debe cambiar según un ajuste interno que quien llama no puede conocer. Sin versión que comparar, ninguna enviada se mira.
        RuleFor(x => x.AcceptedTermsVersion)
            .Must(version => legalTerms.CurrentVersion is null || legalTerms.IsCurrent(version))
            .WithErrorCode(IdentityErrors.TermsNotAccepted);
    }
}

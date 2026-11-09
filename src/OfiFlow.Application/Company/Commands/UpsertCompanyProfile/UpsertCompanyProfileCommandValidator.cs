using FluentValidation;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Company.Commands.UpsertCompanyProfile;

/// <summary>
/// La forma de la petición (spec 024 R2 a R4), antes de tocar la base de datos. Usa las mismas reglas que los objetos de valor del dominio y las mismas cotas que EF Core:
/// lo que el dominio rechazaría después se rechaza aquí con un 400 y su código. Los mensajes no incluyen nunca el valor recibido (ADR-009 R5).
/// </summary>
public sealed class UpsertCompanyProfileCommandValidator : AbstractValidator<UpsertCompanyProfileCommand>
{
    public UpsertCompanyProfileCommandValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(CompanyProfile.LegalNameMaxLength);

        // La longitud primero: una entrada enorme no llega a recorrerse (SpanishTaxId.IsValid también la comprueba antes que nada).
        // Un único error por campo (Stop): un campo ausente es «obligatorio», no además «inválido». Y un valor ausente (null, si falta en el JSON) nunca llega a la regla de forma.
        RuleFor(x => x.TaxId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(SpanishTaxId.InputMaxLength)
            .Must(SpanishTaxId.IsValid)
            .WithErrorCode(CompanyErrors.TaxIdInvalid);

        RuleFor(x => x.AddressLine).NotEmpty().MaximumLength(CompanyProfile.AddressLineMaxLength);

        RuleFor(x => x.PostalCode)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(SpanishPostalCode.IsValid)
            .WithErrorCode(CompanyErrors.PostalCodeInvalid);

        RuleFor(x => x.City).NotEmpty().MaximumLength(CompanyProfile.CityMaxLength);
        RuleFor(x => x.Province).MaximumLength(CompanyProfile.ProvinceMaxLength);

        // Se recortan antes de validar, como al guardar (R2): « 600 123 456 » es un teléfono válido. Un opcional en blanco es «ausente».
        // Mismas reglas que ValidPhone y ValidEmail (longitud primero, luego forma, con el mismo código), pero sobre el valor recortado, que es el que se guarda.
        RuleFor(x => x.Phone)
            .MaximumLength(PhoneNumber.MaxLength)
            .Must(value => value!.Length > PhoneNumber.MaxLength || PhoneNumber.IsValid(value.Trim()))
            .WithErrorCode(CustomerErrors.PhoneInvalid)
            .When(x => !string.IsNullOrWhiteSpace(x.Phone));

        RuleFor(x => x.Email)
            .MaximumLength(Email.MaxLength)
            .Must(value => value!.Length > Email.MaxLength || Email.IsValid(value.Trim()))
            .WithErrorCode(CommonErrors.EmailInvalid)
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

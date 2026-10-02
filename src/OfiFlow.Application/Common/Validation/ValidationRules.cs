using FluentValidation;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Common.Validation;

/// <summary>
/// Reglas reutilizadas por varios Validators. Aplican las mismas reglas que los value objects
/// de Domain, para que una entrada inválida se rechace con 400 antes de llegar al Handler
/// (ADR-009 R3), en vez de acabar en una excepción de Domain.
/// Usan los mismos códigos que Domain (ADR-011): el mensaje lo pone la API desde su diccionario
/// y nunca incluye el valor recibido, que es un dato personal (ADR-009 R5).
/// </summary>
public static class ValidationRules
{
    public static IRuleBuilderOptions<T, string?> ValidEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(Email.MaxLength)
            .Must(value => ExceedsLength(value, Email.MaxLength) || Email.IsValid(value))
            .WithErrorCode(CommonErrors.EmailInvalid);

    public static IRuleBuilderOptions<T, string?> ValidPhone<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(PhoneNumber.MaxLength)
            .Must(value => ExceedsLength(value, PhoneNumber.MaxLength) || PhoneNumber.IsValid(value))
            .WithErrorCode(CustomerErrors.PhoneInvalid);

    // Si ya falla la longitud, el formato no se evalúa: un único error, y la regex nunca
    // recorre una entrada sin límite.
    private static bool ExceedsLength(string? value, int maxLength) => value?.Length > maxLength;
}

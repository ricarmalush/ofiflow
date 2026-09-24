using System.Text.RegularExpressions;
using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Customers;

public sealed partial record PhoneNumber
{
    /// <summary>Fuente única para EF Core y los Validators (ADR-009 R3); coincide con el máximo de la regex.</summary>
    public const int MaxLength = 20;

    public string Value { get; }

    private PhoneNumber(string value)
    {
        Value = value;
    }

    public static PhoneNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(CustomerErrors.PhoneRequired);
        }

        // Solo el código, nunca el valor: es un dato personal y podría acabar en un log (ADR-009 R5).
        if (!IsValid(value))
        {
            throw new DomainException(CustomerErrors.PhoneInvalid);
        }

        return new PhoneNumber(value);
    }

    /// <summary>
    /// Misma regla que <see cref="Create"/>, para que los Validators rechacen con 400 lo que
    /// Domain rechazaría después.
    /// </summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxLength && PhoneRegex().IsMatch(value);

    public override string ToString() => Value;

    [GeneratedRegex(@"^\+?[0-9\s\-()]{6,20}$")]
    private static partial Regex PhoneRegex();
}

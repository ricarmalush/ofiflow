using System.Text.RegularExpressions;

namespace OfiFlow.Domain.Common;

/// <summary>
/// Shared kernel: used by Customer (Customers context) and User (Identity context).
/// </summary>
public sealed partial record Email
{
    /// <summary>Máximo práctico de RFC 5321; fuente única para EF Core y los Validators (ADR-009 R3).</summary>
    public const int MaxLength = 320;

    public string Value { get; }

    private Email(string value)
    {
        Value = value;
    }

    public static Email Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("El email no puede estar vacío.", nameof(value));
        }

        // El mensaje no incluye el valor: es un dato personal y podría acabar en un log (ADR-009 R5).
        if (!IsValid(value))
        {
            throw new ArgumentException("El email no tiene un formato válido.", nameof(value));
        }

        return new Email(value);
    }

    /// <summary>
    /// Misma regla que <see cref="Create"/>, para que los Validators rechacen con 400 lo que
    /// Domain rechazaría después. La longitud se comprueba antes que la regex.
    /// </summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxLength && EmailRegex().IsMatch(value);

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}

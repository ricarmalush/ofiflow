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
            throw new DomainException(CommonErrors.EmailRequired);
        }

        // Solo el código, nunca el valor: es un dato personal y podría acabar en un log (ADR-009 R5).
        if (!IsValid(value))
        {
            throw new DomainException(CommonErrors.EmailInvalid);
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

    // `\z` y no `$`: en .NET `$` también casa justo antes de un "\n" final, y "a@b.com\n"
    // llegaría a EF, a los logs y a cualquier salida tipo cabecera. `\z` solo casa al final absoluto.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z")]
    private static partial Regex EmailRegex();
}

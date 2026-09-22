using System.Text.RegularExpressions;

namespace OfiFlow.Domain.Common;

/// <summary>
/// Shared kernel: used by Customer (Customers context) and User (Identity context).
/// </summary>
public sealed partial record Email
{
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

        if (!EmailRegex().IsMatch(value))
        {
            throw new ArgumentException($"'{value}' no es un email válido.", nameof(value));
        }

        return new Email(value);
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}

using System.Text.RegularExpressions;

namespace OfiFlow.Domain.Customers;

public sealed partial record PhoneNumber
{
    public string Value { get; }

    private PhoneNumber(string value)
    {
        Value = value;
    }

    public static PhoneNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("El teléfono no puede estar vacío.", nameof(value));
        }

        if (!PhoneRegex().IsMatch(value))
        {
            throw new ArgumentException($"'{value}' no es un teléfono válido.", nameof(value));
        }

        return new PhoneNumber(value);
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^\+?[0-9\s\-()]{6,20}$")]
    private static partial Regex PhoneRegex();
}

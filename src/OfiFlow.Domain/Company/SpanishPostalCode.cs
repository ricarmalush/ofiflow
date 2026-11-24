using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Company;

/// <summary>
/// Un código postal español (spec 024 R4): cinco cifras cuyas dos primeras son una provincia que existe (01 a 52, de Álava a Melilla). No comprueba que el código
/// exista en esa provincia: eso exigiría la tabla de Correos.
/// </summary>
public sealed record SpanishPostalCode
{
    public const int Length = 5;

    public string Value { get; }

    private SpanishPostalCode(string value)
    {
        Value = value;
    }

    public static SpanishPostalCode Create(string? value)
    {
        if (!IsValid(value))
        {
            throw new DomainException(CompanyErrors.PostalCodeInvalid);
        }

        return new SpanishPostalCode(value!.Trim());
    }

    /// <summary>Misma regla que <see cref="Create"/>. Los espacios de los extremos no cuentan (se guarda recortado).</summary>
    public static bool IsValid(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var code = value.AsSpan().Trim();
        if (code.Length != Length)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        var province = ((code[0] - '0') * 10) + (code[1] - '0');
        return province is >= 1 and <= 52;
    }

    public override string ToString() => Value;
}

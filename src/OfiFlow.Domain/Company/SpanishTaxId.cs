using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Company;

/// <summary>
/// Un NIF, NIE o CIF español <b>válido de verdad</b> (spec 024 R3): no basta con que tenga la forma, su letra o dígito de control tiene que cuadrar con el algoritmo
/// oficial. Se guarda <b>normalizado</b> (mayúsculas, sin separadores). Es un dato personal cuando la empresa es un autónomo (su NIF es su DNI): el error lleva el
/// código, nunca el valor (ADR-009 R5).
/// <list type="bullet">
/// <item><b>NIF</b>: ocho cifras y una letra (el resto de dividir entre 23 elige la letra).</item>
/// <item><b>NIE</b>: X, Y o Z, siete cifras y una letra (X, Y y Z valen 0, 1 y 2 y se calcula como un NIF).</item>
/// <item><b>CIF</b>: una letra de entidad, siete cifras y un control que es un dígito, una letra, o cualquiera de los dos según la entidad.</item>
/// </list>
/// Los NIF especiales K, L y M (menores y extranjeros sin NIE) no se admiten por ahora (D6).
/// </summary>
public sealed record SpanishTaxId
{
    /// <summary>Lo que ocupa un identificador normalizado: siempre nueve caracteres. Fuente única para EF Core.</summary>
    public const int MaxLength = 9;

    /// <summary>
    /// Lo más largo que se acepta <b>antes</b> de normalizar (con espacios, guiones o puntos de por medio). Un tope para que nada recorra una entrada sin límite
    /// y para que el validador rechace por longitud antes que por forma.
    /// </summary>
    public const int InputMaxLength = 20;

    private const string ControlLetters = "TRWAGMYFPDXBNJZSQVHLCKE";
    private const string CifLetterControls = "JABCDEFGHI";

    // Entidades cuyo control es SIEMPRE una letra, SIEMPRE un dígito, o cualquiera de las dos (el resto de las letras de CifEntities).
    private const string CifLetterOnlyEntities = "PQRSNW";
    private const string CifDigitOnlyEntities = "ABEH";
    private const string CifEntities = "ABCDEFGHJNPQRSUVW";

    public string Value { get; }

    private SpanishTaxId(string value)
    {
        Value = value;
    }

    public static SpanishTaxId Create(string? value)
    {
        // Solo el código, nunca el valor: es un dato personal y podría acabar en un log (ADR-009 R5).
        if (!IsValid(value))
        {
            throw new DomainException(CompanyErrors.TaxIdInvalid);
        }

        return new SpanishTaxId(Normalize(value!));
    }

    /// <summary>
    /// Misma regla que <see cref="Create"/>, para que el validador rechace con 400 lo que el dominio rechazaría después. La longitud se comprueba antes que nada.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is not null && value.Length <= InputMaxLength && HasValidControl(Normalize(value));

    /// <summary>Mayúsculas y sin espacios, guiones ni puntos: un <c>12.345.678-z</c> con espacios de más es <c>12345678Z</c>. No valida nada.</summary>
    public static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '.')
            {
                continue;
            }

            // Solo las letras ASCII se pasan a mayúscula: ToUpperInvariant convertiría letras de otros alfabetos que se le parecen (la «ı» turca en «I») en una letra válida.
            buffer[length++] = c is >= 'a' and <= 'z' ? (char)(c - ('a' - 'A')) : c;
        }

        return new string(buffer, 0, length);
    }

    public override string ToString() => Value;

    private static bool HasValidControl(string id)
    {
        if (id.Length != MaxLength)
        {
            return false;
        }

        // Solo ASCII: char.IsDigit aceptaría cifras de otros alfabetos.
        var first = id[0];

        if (char.IsAsciiDigit(first))
        {
            return IsNif(id);
        }

        if (first is 'X' or 'Y' or 'Z')
        {
            return IsNie(id);
        }

        return CifEntities.Contains(first) && IsCif(id);
    }

    private static bool IsNif(string id) =>
        AllAsciiDigits(id.AsSpan(0, 8)) && id[8] == ControlLetterFor(int.Parse(id.AsSpan(0, 8)));

    private static bool IsNie(string id)
    {
        if (!AllAsciiDigits(id.AsSpan(1, 7)))
        {
            return false;
        }

        // X → 0, Y → 1, Z → 2 delante de las siete cifras, y se calcula como un NIF de ocho cifras.
        var prefix = id[0] - 'X';
        var number = (prefix * 10_000_000) + int.Parse(id.AsSpan(1, 7));

        return id[8] == ControlLetterFor(number);
    }

    private static bool IsCif(string id)
    {
        var digits = id.AsSpan(1, 7);
        if (!AllAsciiDigits(digits))
        {
            return false;
        }

        // Posiciones impares (1.ª, 3.ª, 5.ª, 7.ª): se duplica y se suman las cifras del resultado. Pares: se suman tal cual.
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[i] - '0';
            if (i % 2 == 0)
            {
                var doubled = digit * 2;
                sum += (doubled / 10) + (doubled % 10);
            }
            else
            {
                sum += digit;
            }
        }

        var controlDigit = (10 - (sum % 10)) % 10;
        var control = id[8];
        var digitMatches = control == (char)('0' + controlDigit);
        var letterMatches = control == CifLetterControls[controlDigit];

        if (CifLetterOnlyEntities.Contains(id[0]))
        {
            return letterMatches;
        }

        if (CifDigitOnlyEntities.Contains(id[0]))
        {
            return digitMatches;
        }

        return digitMatches || letterMatches;
    }

    private static char ControlLetterFor(int number) => ControlLetters[number % 23];

    private static bool AllAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

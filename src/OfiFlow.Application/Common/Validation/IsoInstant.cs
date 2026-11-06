using System.Globalization;
using System.Text.RegularExpressions;

namespace OfiFlow.Application.Common.Validation;

/// <summary>
/// Lee un instante ISO-8601 que lleva <b>su desfase explícito</b> (<c>2026-11-10T09:00:00+01:00</c> o <c>…Z</c>) y lo devuelve en UTC
/// (spec 020 R4). Una hora <b>sin</b> desfase no se acepta: el servidor no sabe en qué huso la escribió quien la mandó, y
/// <see cref="DateTimeOffset.TryParse(string?, IFormatProvider?, DateTimeStyles, out DateTimeOffset)"/> la daría por hora local del
/// <i>servidor</i>, un error silencioso de horas en una cita. Por eso primero se comprueba la forma y después se lee.
/// </summary>
public static partial class IsoInstant
{
    /// <summary>Longitud máxima aceptada: la forma más larga (<c>2026-11-10T09:00:00.1234567+01:00</c>) tiene 33 caracteres.</summary>
    public const int MaxLength = 40;

    public static bool TryParse(string? value, out DateTime utc)
    {
        utc = default;

        // \z y no $: «$» aceptaría un salto de línea final.
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !Shape().IsMatch(value))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    /// <summary>Como <see cref="TryParse"/>, pero lanza si no es válido: para quien ya lo comprobó con el validador.</summary>
    public static DateTime Parse(string value) =>
        TryParse(value, out var utc) ? utc : throw new FormatException("No es un instante ISO-8601 con desfase explícito.");

    // [0-9] y no \d: en .NET \d acepta también los dígitos de otros alfabetos (p. ej. los árabes), que esto no debe dar por válidos.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

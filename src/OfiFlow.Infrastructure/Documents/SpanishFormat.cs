using System.Globalization;
using System.Text;

namespace OfiFlow.Infrastructure.Documents;

/// <summary>
/// Cantidades, porcentajes y fechas en el formato español, con cultura <b>fija</b> (spec 025 R6): el resultado no cambia con la configuración regional del servidor ni del hilo. El dinero está en
/// <see cref="MoneyFormat"/>, que solo usa enteros.
/// </summary>
internal static class SpanishFormat
{
    /// <summary>Hasta tres decimales (el máximo de una cantidad, spec 023) sin ceros sobrantes: «10», «2,5», «1.234,567».</summary>
    public static string Quantity(decimal quantity)
    {
        // «#,##0.###» en la cultura invariante da «1,234.5»: se cambian los dos separadores por los españoles, sin pasar nunca por la cultura del servidor.
        var invariant = quantity.ToString("#,##0.###", CultureInfo.InvariantCulture);
        var text = new StringBuilder(invariant.Length);

        foreach (var c in invariant)
        {
            text.Append(c switch { ',' => '.', '.' => ',', _ => c });
        }

        return text.ToString();
    }

    /// <summary>«21 %» con un espacio no separable, como los importes.</summary>
    public static string Percent(int percent) => percent.ToString(CultureInfo.InvariantCulture) + (char)0xA0 + "%";

    /// <summary>«dd/MM/yyyy». La barra de la cadena de formato es la del idioma; con la cultura invariante es siempre «/».</summary>
    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}

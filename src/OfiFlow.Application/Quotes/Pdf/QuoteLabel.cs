using System.Globalization;

namespace OfiFlow.Application.Quotes.Pdf;

/// <summary>
/// El número de un presupuesto tal como se enseña: «P-0012» (el mismo formato que la pantalla, <c>formatQuoteNumber</c>). Es un entero correlativo dentro de la empresa; esto es solo para mostrarlo.
/// Lo usan el título del PDF y el nombre del fichero (spec 025 D11): <b>solo el número</b>, nunca texto del usuario.
/// </summary>
public static class QuoteLabel
{
    public static string Format(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);

        return "P-" + number.ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>El nombre del fichero que se descarga: «presupuesto-P-0012.pdf».</summary>
    public static string FileName(int number) => "presupuesto-" + Format(number) + ".pdf";
}

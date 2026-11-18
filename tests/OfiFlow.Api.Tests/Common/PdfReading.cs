using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// El PDF que descarga la API, leído de vuelta con PdfPig (spec 025): texto por página y número de páginas. Se comprueba por su contenido, nunca por sus bytes (llevan identificadores y fechas).
/// </summary>
internal sealed class PdfReading
{
    private PdfReading(byte[] bytes)
    {
        Bytes = bytes;
        using var document = PdfDocument.Open(bytes);
        Pages = [.. document.GetPages().Select(page => Normalize(ContentOrderTextExtractor.GetText(page, addDoubleNewline: false)))];
    }

    public byte[] Bytes { get; }

    public IReadOnlyList<string> Pages { get; }

    public int PageCount => Pages.Count;

    public string Text => string.Join("\n", Pages);

    public string[] Lines => Text.Split('\n');

    public static PdfReading Of(byte[] bytes) => new(bytes);

    public static async Task<PdfReading> OfAsync(HttpResponseMessage response) => new(await response.Content.ReadAsByteArrayAsync());

    // Los espacios no separables se leen como espacios normales, los repetidos se juntan y las líneas se separan siempre por '\n' (el extractor usa el salto de la plataforma).
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousSpace = false;

        foreach (var c in text.Replace("\r", string.Empty, StringComparison.Ordinal))
        {
            var isSpace = c is ' ' or (char)0xA0;
            if (isSpace && previousSpace)
            {
                continue;
            }

            builder.Append(isSpace ? ' ' : c);
            previousSpace = isSpace;
        }

        return builder.ToString();
    }

    /// <summary>
    /// El importe en el formato del documento («1.234,56 €»), escrito aquí <b>aparte</b> del de producción con aritmética distinta (texto, no módulo): dos copias independientes que tienen que coincidir.
    /// </summary>
    public static string Money(long cents)
    {
        var sign = cents < 0 ? "-" : string.Empty;
        var digits = Math.Abs((decimal)cents).ToString("0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(3, '0');
        var euros = digits[..^2];
        var grouped = new StringBuilder();
        for (var i = 0; i < euros.Length; i++)
        {
            if (i > 0 && (euros.Length - i) % 3 == 0)
            {
                grouped.Append('.');
            }

            grouped.Append(euros[i]);
        }

        return $"{sign}{grouped},{digits[^2..]} €";
    }
}

using System.Text;
using OfiFlow.Application.Quotes.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>Datos de ejemplo del documento: un presupuesto completo y razonable, sobre el que cada test cambia solo lo que quiere probar.</summary>
internal static class PdfTestData
{
    public static QuotePdfIssuer Issuer { get; } = new(
        "Fontanería Marta Gil S.L.", "B84531946", "Calle de Alcalá 123, 2º B", "28009", "Madrid", "Madrid", "912345678", "marta@fontaneria-gil.example");

    public static QuotePdfModel Model(
        string? title = null,
        bool isDraft = false,
        IReadOnlyList<QuotePdfLine>? lines = null,
        string? notes = "Pago a 30 días. Los materiales no incluidos se presupuestan aparte.",
        DateOnly? validUntil = null,
        QuotePdfIssuer? issuer = null,
        QuotePdfCustomer? customer = null)
    {
        lines ??= [new QuotePdfLine("Cambio de la caldera de gas", 1m, 185_000, 21, 185_000), new QuotePdfLine("Mano de obra", 2.5m, 4_500, 21, 11_250)];
        var breakdown = lines.GroupBy(l => l.VatPercent).OrderBy(g => g.Key).Select(g => new QuotePdfVatLine(g.Key, g.Sum(l => l.NetCents), RoundedVat(g.Sum(l => l.NetCents), g.Key))).ToList();
        var net = breakdown.Sum(b => b.BaseCents);
        var vat = breakdown.Sum(b => b.VatCents);

        return new QuotePdfModel(
            issuer ?? Issuer,
            "P-0012",
            title ?? "Sustitución de caldera",
            new DateOnly(2026, 11, 10),
            validUntil ?? new DateOnly(2026, 12, 10),
            isDraft,
            customer ?? new QuotePdfCustomer("Construcciones Pérez e Hijos", "Avenida de América 45\n28028 Madrid"),
            lines,
            breakdown,
            net,
            vat,
            net + vat,
            notes);
    }

    // Solo para armar datos de ejemplo coherentes en los tests: el generador no calcula nada (R5).
    private static long RoundedVat(long baseCents, int percent) => (baseCents * percent + 50) / 100;
}

/// <summary>El PDF leído de vuelta con PdfPig: texto por página, número de páginas y metadatos.</summary>
internal sealed class ReadPdf
{
    private ReadPdf(byte[] bytes)
    {
        Bytes = bytes;
        using var document = PdfDocument.Open(bytes);
        PageCount = document.NumberOfPages;
        Pages = document.GetPages().Select(page => Normalize(ContentOrderTextExtractor.GetText(page, addDoubleNewline: false))).ToList();
        Title = document.Information.Title;
        Creator = document.Information.Creator;
        Producer = document.Information.Producer;
        Author = document.Information.Author;
        Subject = document.Information.Subject;
        Keywords = document.Information.Keywords;
    }

    public byte[] Bytes { get; }

    public int PageCount { get; }

    public IReadOnlyList<string> Pages { get; }

    public string Text => string.Join("\n", Pages);

    public string? Title { get; }

    public string? Creator { get; }

    public string? Producer { get; }

    public string? Author { get; }

    public string? Subject { get; }

    public string? Keywords { get; }

    public static ReadPdf From(byte[] bytes) => new(bytes);

    // Los espacios no separables se leen como espacios normales y los espacios repetidos se juntan: lo que importa es la secuencia de palabras.
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousSpace = false;

        // El extractor separa las líneas con el salto de la plataforma (en Windows, \r\n): se unifica para poder partir siempre por '\n'.
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
}

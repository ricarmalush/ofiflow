using System.Reflection;
using PdfSharp.Fonts;

namespace OfiFlow.Infrastructure.Documents;

/// <summary>
/// La única fuente del PDF (spec 025 D8): Liberation Sans (licencia OFL), regular y negrita, <b>dentro del ensamblado</b>. En un servidor Linux no hay fuentes del sistema que se puedan dar por
/// supuestas, y depender de las de la máquina haría que el mismo presupuesto saliera distinto en cada servidor. PDFsharp pide las fuentes por nombre a través de <see cref="IFontResolver"/>; este
/// resolvedor contesta siempre con las dos incluidas, también cuando se pide otra familia (nunca falla por una fuente que no existe).
/// </summary>
internal sealed class PdfFonts : IFontResolver
{
    public const string FamilyName = "Liberation Sans";

    private const string RegularFace = "LiberationSans-Regular";
    private const string BoldFace = "LiberationSans-Bold";

    private static readonly Lazy<byte[]> Regular = new(() => Load(RegularFace));
    private static readonly Lazy<byte[]> Bold = new(() => Load(BoldFace));
    private static readonly object Gate = new();

    /// <summary>
    /// Registra el resolvedor en PDFsharp, que lo guarda en un valor <b>global</b> y no admite cambiarlo una vez usadas las fuentes: se hace una sola vez por proceso, de forma segura entre hilos.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is not PdfFonts)
            {
                GlobalFontSettings.FontResolver = new PdfFonts();
            }
        }
    }

    /// <summary>Los bytes de una de las dos fuentes, para los tests que comprueban qué caracteres cubren.</summary>
    internal static byte[] FontBytes(bool bold) => (bold ? Bold : Regular).Value;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldFace : RegularFace);

    public byte[]? GetFont(string faceName) => faceName switch
    {
        RegularFace => Regular.Value,
        BoldFace => Bold.Value,
        _ => null
    };

    private static byte[] Load(string face)
    {
        var assembly = typeof(PdfFonts).Assembly;
        using var stream = assembly.GetManifestResourceStream($"OfiFlow.Fonts.{face}.ttf")
            ?? throw new InvalidOperationException($"Falta la fuente incrustada {face}: revisa el EmbeddedResource de OfiFlow.Infrastructure.csproj.");

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        return bytes;
    }
}

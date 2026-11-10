using System.Security.Cryptography;
using OfiFlow.Infrastructure.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.TrueType;
using UglyToad.PdfPig.Fonts.TrueType.Parser;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>
/// La fuente incluida (spec 025 D8): es exactamente la que se descargó de Liberation Fonts, tiene licencia libre al lado, no depende del sistema y <b>cubre cada carácter</b> que el saneado
/// deja pasar (un carácter sin glifo saldría como un hueco en un documento que va a un cliente).
/// </summary>
public class PdfFontsTests
{
    // Las huellas de LiberationSans 2.1.5 (fichero liberation-fonts-ttf-2.1.5.tar.gz del repositorio oficial). Cambiar un byte de la fuente exige cambiar también el README que las documenta.
    private const string RegularSha256 = "76d04c18ea243f426b7de1f3ad208e927008f961dc5945e5aad352d0dfde8ee8";
    private const string BoldSha256 = "788abee4c806d660e8aee46689dd8540cd4bb98da03dcc9d171ce3efd99a9173";

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void TheEmbeddedFontsAreTheExactOnesThatWereDownloaded()
    {
        Assert.Equal(RegularSha256, Hash(PdfFonts.FontBytes(bold: false)));
        Assert.Equal(BoldSha256, Hash(PdfFonts.FontBytes(bold: true)));
    }

    [Fact]
    public void TheReadmeNextToTheFontsDocumentsTheSameHashes()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "OfiFlow.Infrastructure", "Documents", "Fonts", "README.md"));

        Assert.Contains(RegularSha256, readme, StringComparison.Ordinal);
        Assert.Contains(BoldSha256, readme, StringComparison.Ordinal);
        Assert.Contains("SIL Open Font License 1.1", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLicenseOfTheFontTravelsWithIt()
    {
        var fonts = Path.Combine(RepositoryRoot(), "src", "OfiFlow.Infrastructure", "Documents", "Fonts");
        var license = File.ReadAllText(Path.Combine(fonts, "LICENSE-LiberationSans.txt"));

        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", license, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fonts, "AUTHORS-LiberationSans.txt")));
    }

    [Fact]
    public void TheFontsAreResourcesOfTheAssemblyAndNotFilesOfTheMachine()
    {
        var names = typeof(PdfFonts).Assembly.GetManifestResourceNames();

        Assert.Contains("OfiFlow.Fonts.LiberationSans-Regular.ttf", names);
        Assert.Contains("OfiFlow.Fonts.LiberationSans-Bold.ttf", names);
    }

    [Fact]
    public void TheResolverAlwaysAnswersWithTheTwoEmbeddedFacesWhateverFamilyIsAsked()
    {
        var resolver = new PdfFonts();

        Assert.NotNull(resolver.GetFont(resolver.ResolveTypeface("Liberation Sans", false, false)!.FaceName));
        Assert.NotNull(resolver.GetFont(resolver.ResolveTypeface("Liberation Sans", true, false)!.FaceName));

        // Una familia que no existe (o cursiva) no hace fallar la generación.
        Assert.NotNull(resolver.GetFont(resolver.ResolveTypeface("Comic Sans", false, true)!.FaceName));
        Assert.NotEqual(resolver.ResolveTypeface("x", false, false)!.FaceName, resolver.ResolveTypeface("x", true, false)!.FaceName);
        Assert.Null(resolver.GetFont("OtraFuente"));
    }

    [Fact]
    public void TheResolverIsRegisteredOnlyOncePerProcess()
    {
        // PDFsharp guarda el resolvedor en un valor global y no admite cambiarlo una vez usadas las fuentes: registrarlo otra vez tiene que dejar el mismo.
        PdfFonts.EnsureRegistered();
        var first = PdfSharp.Fonts.GlobalFontSettings.FontResolver;
        PdfFonts.EnsureRegistered();

        Assert.IsType<PdfFonts>(first);
        Assert.Same(first, PdfSharp.Fonts.GlobalFontSettings.FontResolver);
    }

    [Fact]
    public void EachFaceReturnsItsOwnBytes()
    {
        var resolver = new PdfFonts();
        var regular = resolver.GetFont(resolver.ResolveTypeface("Liberation Sans", false, false)!.FaceName);
        var bold = resolver.GetFont(resolver.ResolveTypeface("Liberation Sans", true, false)!.FaceName);

        Assert.Equal(RegularSha256, Hash(regular!));
        Assert.Equal(BoldSha256, Hash(bold!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFontHasAGlyphForEveryCharacterThatTheCleaningLetsThrough(bool bold)
    {
        var font = TrueTypeFontParser.Parse(new TrueTypeDataBytes(new MemoryInputBytes(PdfFonts.FontBytes(bold))));
        var missing = new List<string>();

        for (var codePoint = 0; codePoint <= 0x2100; codePoint++)
        {
            if (PdfText.IsPrintable(codePoint) && !HasGlyph(font, codePoint))
            {
                missing.Add($"U+{codePoint:X4}");
            }
        }

        Assert.True(missing.Count == 0, $"La fuente {(bold ? "negrita" : "regular")} no tiene glifo para: {string.Join(", ", missing)}");
    }

    [Fact]
    public void TheEuroSignAndTheSpanishLettersAreInTheFont()
    {
        var font = TrueTypeFontParser.Parse(new TrueTypeDataBytes(new MemoryInputBytes(PdfFonts.FontBytes(bold: false))));

        foreach (var c in "€ñÑáéíóúüÁÉÍÓÚÜ¿¡ºª·")
        {
            Assert.True(HasGlyph(font, c), $"Falta {c}");
        }
    }

    [Fact]
    public void TheFontDoesNotHaveAnEmojiSoTheCleaningReallyHasToReplaceIt()
    {
        // Control negativo del test de cobertura: si «tener glifo» devolviera siempre verdadero, ese test no demostraría nada.
        var font = TrueTypeFontParser.Parse(new TrueTypeDataBytes(new MemoryInputBytes(PdfFonts.FontBytes(bold: false))));

        Assert.False(HasGlyph(font, 0x1F600));
        Assert.False(HasGlyph(font, 0x4E2D));
        Assert.False(HasGlyph(font, 0x0645));
    }

    private static bool HasGlyph(TrueTypeFont font, int codePoint) =>
        font.TableRegister.CMapTable!.TryGetGlyphIndex(codePoint, out var glyph) && glyph != 0;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OfiFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No se encuentra la raíz del repositorio (OfiFlow.slnx).");
    }
}

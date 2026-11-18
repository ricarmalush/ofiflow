using OfiFlow.Infrastructure.Documents;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>
/// El saneado del texto libre antes de imprimirlo (spec 025 R7): se imprime tal cual, sin controles ni caracteres que la fuente no tenga, con los saltos de línea donde tienen sentido y sin palabras
/// que se salgan de la página.
/// </summary>
public class PdfTextTests
{
    // Los caracteres «difíciles» se construyen con su código en vez de escribirlos: así el fichero es legible y ningún editor los cambia.
    private static string C(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static string Single(string? text) => PdfText.Clean(text, allowNewlines: false);

    private static string Multi(string? text) => PdfText.Clean(text, allowNewlines: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void NothingToPrintGivesAnEmptyString(string? text)
    {
        Assert.Equal(string.Empty, Single(text));
        Assert.Equal(string.Empty, Multi(text));
    }

    [Theory]
    [InlineData("Cambio de caldera")]
    [InlineData("Instalación eléctrica, ñoño: ¿qué tal? ¡Bien!")]
    [InlineData("ÁÉÍÓÚÜÑ áéíóúüñ çÇ àèìòù âêîôû")]
    [InlineData("50 % de descuento (IVA incl.) - total 1.234,56 €")]
    [InlineData("Ł ł Ő ő Ž ž Ğ ğ Ş ş")]
    public void OrdinarySpanishTextIsPrintedAsItIs(string text)
    {
        Assert.Equal(text, Single(text));
        Assert.Equal(text, Multi(text));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("&amp; &lt;b&gt; %s %d {0} ${x}")]
    [InlineData("\\n \\u0041 \\\\ \\\"")]
    [InlineData("[link](http://example.test) <a href=\"x\">y</a>")]
    [InlineData("=SUMA(A1:A2) +cmd|' /C calc'!A0")]
    public void MarkupAndFormatCharactersAreJustText(string text)
    {
        // No se interpreta nada: lo que se escribe es lo que sale.
        Assert.Equal(text, Single(text));
    }

    [Fact]
    public void TypographicPunctuationAndTheEuroAreKept()
    {
        var text = "a" + C(0x2013) + "b" + C(0x2014) + "c " + C(0x2018) + "x" + C(0x2019) + " " + C(0x201C) + "y" + C(0x201D) + " " + C(0x2022) + " ok" + C(0x2026) + " 5" + C(0x20AC);

        Assert.Equal(text, Single(text));
    }

    [Fact]
    public void ControlCharactersAreRemoved()
    {
        Assert.Equal("ab", Single("a" + C(0) + "b"));
        Assert.Equal("ab", Single("a" + C(7) + C(8) + C(0x1B) + "b"));
        Assert.Equal("ab", Single("a" + C(0x7F) + C(0x9F) + "b"));
    }

    [Fact]
    public void FormatCharactersThatReorderTheTextAreRemoved()
    {
        // U+202E (invertir el sentido), U+2066..2069 (aislamientos), U+200B/200C/200D (anchura cero), U+FEFF, U+00AD (guion blando).
        var hostile = "pago" + C(0x202E) + "fdp.exe" + C(0x2066) + C(0x2069) + C(0x200B) + C(0x200C) + C(0x200D) + C(0xFEFF) + C(0xAD) + "fin";

        Assert.Equal("pagofdp.exefin", Single(hostile));
    }

    [Theory]
    [InlineData(0xE000)] // uso privado
    [InlineData(0xF8FF)]
    [InlineData(0x0378)] // sin asignar
    [InlineData(0x10FFFF)]
    public void PrivateUseAndUnassignedCharactersAreRemovedNotShownAsQuestionMarks(int codePoint)
    {
        Assert.Equal("ab", Single("a" + C(codePoint) + "b"));
    }

    [Fact]
    public void ATabIsASpace()
    {
        Assert.Equal("a b", Single("a\tb"));
        Assert.Equal("a b", Multi("a\tb"));
    }

    [Fact]
    public void UnusualSpacesBecomeASingleSpace()
    {
        // U+00A0 (no separable), U+2003 (espacio largo), U+3000 (ideográfico): un espacio normal; los repetidos se juntan.
        Assert.Equal("a b c d", Single("a" + C(0xA0) + "b" + C(0x2003) + "c" + C(0x3000) + C(0x20) + "d"));
    }

    [Theory]
    [InlineData(0x1F600)] // 😀 emoji
    [InlineData(0x1F4A9)]
    [InlineData(0x0645)] // م árabe
    [InlineData(0x4E2D)] // 中 chino
    [InlineData(0x3042)] // あ hiragana
    [InlineData(0x05D0)] // א hebreo
    [InlineData(0x2603)] // ☃ símbolo
    [InlineData(0x0416)] // Ж cirílico (la fuente lo tiene, pero la plataforma es para España)
    public void ACharacterTheFontDoesNotCoverIsReplacedByAQuestionMark(int codePoint)
    {
        Assert.Equal("a?b", Single("a" + C(codePoint) + "b"));
    }

    [Fact]
    public void ALoneSurrogateIsReplacedAndNeverThrows()
    {
        Assert.Equal("a?b", Single("a" + (char)0xD800 + "b"));
        Assert.Equal("a?b", Single("a" + (char)0xDC00 + "b"));
    }

    [Fact]
    public void ARightToLeftTextBecomesQuestionMarksInsteadOfReorderingTheLine()
    {
        var arabic = C(0x645) + C(0x631) + C(0x62D) + C(0x628) + C(0x627);

        Assert.Equal("Hola ????? adiós", Single("Hola " + arabic + " adiós"));
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\rb", "a b")]
    [InlineData("a\n\n\nb", "a b")]
    [InlineData("  a  \n  b  ", "a b")]
    public void InASingleLineFieldLineBreaksBecomeASpace(string text, string expected)
    {
        Assert.Equal(expected, Single(text));
    }

    [Theory]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\n\nb", "a\n\nb")]
    [InlineData("a\n\n\n\n\nb", "a\n\nb")]
    [InlineData("a\r\n\r\n\r\n\r\nb", "a\n\nb")]
    [InlineData("\n\na\n\n", "a")]
    public void InAMultilineFieldLineBreaksAreKeptWithAtMostTwoInARow(string text, string expected)
    {
        Assert.Equal(expected, Multi(text));
    }

    [Fact]
    public void TheUnicodeLineSeparatorsAreLineBreaksToo()
    {
        Assert.Equal("a\nb\nc\nd", Multi("a" + C(0x2028) + "b" + C(0x2029) + "c" + C(0x85) + "d"));
    }

    [Fact]
    public void AWindowsLineBreakCountsAsOneNotTwo()
    {
        // Cinco saltos \r\n seguidos son cinco saltos (se recortan a dos), no diez.
        Assert.Equal("a\n\nb", Multi("a\r\n\r\n\r\n\r\n\r\nb"));
        Assert.Equal("a\nb", Multi("a\r\nb"));
    }

    [Fact]
    public void ManyBlankLinesCannotMakeADocumentOfEmptyPages()
    {
        var text = "inicio" + new string('\n', 2000) + "fin";

        Assert.Equal("inicio\n\nfin", Multi(text));
    }

    [Fact]
    public void SpacesAroundLineBreaksDoNotCountAsBlankLinesToo()
    {
        Assert.Equal("a\n\nb", Multi("a\n \n \n \n \nb"));
    }

    // ---- Palabras que no caben (BreakLongWords) -----------------------------------------------------------------------------------------------------------------------------------------

    // Un ancho de mentira: la «W» ocupa 10, la «i» 2 y todo lo demás 5.
    private static double Width(char c) => c switch { 'W' => 10, 'i' => 2, _ => 5 };

    [Theory]
    [InlineData("abcdefghij", 25, "abcde fghij")]
    [InlineData("abcdefghijk", 25, "abcde fghij k")]
    [InlineData("abcde", 25, "abcde")]
    [InlineData("abcd efghi", 25, "abcd efghi")]
    [InlineData("abc", 5, "a b c")]
    [InlineData("WWWWW", 20, "WW WW W")]
    [InlineData("iiiiiiiiiiii", 12, "iiiiii iiiiii")]
    [InlineData("WiWiWi", 24, "WiWi Wi")]
    public void ALongWordIsBrokenWhereItStopsFittingMeasuringEachLetter(string text, double maxWidth, string expected)
    {
        Assert.Equal(expected, PdfText.BreakLongWords(text, maxWidth, Width));
    }

    [Fact]
    public void AWordThatFitsIsNeverTouchedAndTheWidthRestartsAtEachSpaceAndLineBreak()
    {
        Assert.Equal("abcde abcde", PdfText.BreakLongWords("abcde abcde", 25, Width));
        Assert.Equal("abcde\nabcde", PdfText.BreakLongWords("abcde\nabcde", 25, Width));
        Assert.Equal(string.Empty, PdfText.BreakLongWords(string.Empty, 25, Width));
    }

    [Fact]
    public void AFiveHundredLetterWordKeepsEveryLetterAndNoPieceIsWiderThanTheLimit()
    {
        var word = string.Concat(Enumerable.Range(0, 500).Select(i => (char)('a' + i % 26)));

        var broken = PdfText.BreakLongWords(word, 100, Width);

        Assert.Equal(word, broken.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.All(broken.Split(' '), piece => Assert.True(piece.Sum(Width) <= 100));
    }

    [Fact]
    public void ARunOfWideLettersIsBrokenSooner()
    {
        // La misma anchura da veinte «i» o cinco «W»: el corte depende del ancho real, no de contar letras.
        Assert.Equal(1, PdfText.BreakLongWords(new string('i', 50), 100, Width).Split(' ').Length);
        Assert.Equal(10, PdfText.BreakLongWords(new string('W', 50), 50, Width).Split(' ').Length);
    }

    [Fact]
    public void ALetterWiderThanTheWholeLimitStillGoesOutAloneAndNothingLoops()
    {
        Assert.Equal("W W W", PdfText.BreakLongWords("WWW", 5, Width));
    }

    [Fact]
    public void TheBreakingArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => PdfText.BreakLongWords("a", 10, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfText.BreakLongWords("a", 0, Width));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfText.BreakLongWords("a", -1, Width));
    }

    [Fact]
    public void TheTextOfTheLongestFieldIsHandledAsItIs()
    {
        var notes = string.Concat(Enumerable.Repeat("palabra ", 250));

        var cleaned = Multi(notes);

        Assert.StartsWith("palabra palabra", cleaned, StringComparison.Ordinal);
        Assert.Equal(notes.Trim(), cleaned);
    }

    [Theory]
    [InlineData(0x20, true)]
    [InlineData(0x7E, true)]
    [InlineData(0x7F, false)]
    [InlineData(0x80, false)]
    [InlineData(0x9F, false)]
    [InlineData(0xA0, true)]
    [InlineData(0xAD, false)]
    [InlineData(0xBF, true)]
    [InlineData(0xFF, true)]
    [InlineData(0x100, true)]
    [InlineData(0x17F, true)]
    [InlineData(0x180, false)]
    [InlineData(0x20AC, true)]
    [InlineData(0x20AD, false)]
    [InlineData(0x2013, true)]
    [InlineData(0x1F600, false)]
    public void ThePrintableSetIsExactlyTheDocumentedOne(int codePoint, bool expected)
    {
        Assert.Equal(expected, PdfText.IsPrintable(codePoint));
    }
}

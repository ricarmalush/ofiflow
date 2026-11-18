using System.Globalization;
using System.Text;
using System.Text.Json;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Domain.Quotes;
using OfiFlow.Infrastructure.Documents;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>
/// El PDF del presupuesto, <b>leído de vuelta</b> (spec 025): lo que contiene, que no se inventa ningún importe, que el borrador lleva su marca en cada página, que el texto libre no rompe ni se sale de la
/// página y que el documento es inerte. Nunca se compara por bytes (llevan identificadores y fechas): se lee su texto y su estructura con PdfPig y con el propio PDFsharp.
/// </summary>
public class PdfSharpQuotePdfRendererTests
{
    private static readonly PdfSharpQuotePdfRenderer Renderer = new();
    private static readonly char NonBreakingSpace = (char)0xA0;

    private static string C(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static string Money(long cents) => MoneyFormat.Format(cents).Replace(NonBreakingSpace, ' ');

    private static ReadPdf Render(QuotePdfModel model) => ReadPdf.From(Renderer.Render(model));

    // El texto sin espacios ni saltos: para buscar un texto que el saneado puede haber partido con un espacio (una palabra larguísima).
    private static string Squash(string text) => text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

    // ---- El documento y su contenido (R4) ---------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ItIsAValidPdfOfOnePage()
    {
        var bytes = Renderer.Render(PdfTestData.Model());

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(bytes, bytes.Length - 16, 16), StringComparison.Ordinal);

        var pdf = ReadPdf.From(bytes);
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void ItPrintsTheIssuerWithItsFiscalData()
    {
        var text = Render(PdfTestData.Model()).Text;

        Assert.Contains("Fontanería Marta Gil S.L.", text, StringComparison.Ordinal);
        Assert.Contains("NIF B84531946", text, StringComparison.Ordinal);
        Assert.Contains("Calle de Alcalá 123, 2º B", text, StringComparison.Ordinal);
        Assert.Contains("28009 Madrid (Madrid)", text, StringComparison.Ordinal);
        Assert.Contains("Tel. 912345678", text, StringComparison.Ordinal);
        Assert.Contains("marta@fontaneria-gil.example", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItPrintsTheDocumentTheTitleAndTheDates()
    {
        var text = Render(PdfTestData.Model(validUntil: new DateOnly(2027, 1, 31))).Text;

        Assert.Contains("PRESUPUESTO", text, StringComparison.Ordinal);
        Assert.Contains("P-0012", text, StringComparison.Ordinal);
        Assert.Contains("Sustitución de caldera", text, StringComparison.Ordinal);
        Assert.Contains("Fecha: 10/11/2026", text, StringComparison.Ordinal);
        Assert.Contains("Válido hasta: 31/01/2027", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItPrintsTheCustomerWithItsNameAndItsAddressOnTheirOwnLines()
    {
        var lines = Render(PdfTestData.Model()).Text.Split('\n');

        Assert.Contains("Cliente", lines);
        Assert.Contains("Construcciones Pérez e Hijos", lines);
        Assert.Contains("Avenida de América 45", lines);
        Assert.Contains("28028 Madrid", lines);
    }

    [Fact]
    public void ItPrintsEveryLineWithItsQuantityUnitPriceVatAndNet()
    {
        var model = PdfTestData.Model(lines:
        [
            new QuotePdfLine("Caldera mural de condensación", 1m, 185_000, 21, 185_000),
            new QuotePdfLine("Mano de obra", 2.5m, 4_500, 10, 11_250),
            new QuotePdfLine("Tubería de cobre por metro", 12.125m, 1_234, 4, 14_963)
        ]);

        var lines = Render(model).Text.Split('\n');

        Assert.Contains("Descripción Cant. Precio IVA Importe", lines);
        Assert.Contains("Caldera mural de condensación 1 1.850,00 € 21 % 1.850,00 €", lines);
        Assert.Contains("Mano de obra 2,5 45,00 € 10 % 112,50 €", lines);
        Assert.Contains("Tubería de cobre por metro 12,125 12,34 € 4 % 149,63 €", lines);
    }

    [Fact]
    public void ItPrintsTheVatBreakdownByTypeAndTheTotals()
    {
        var model = PdfTestData.Model(lines:
        [
            new QuotePdfLine("A", 1m, 10_000, 21, 10_000),
            new QuotePdfLine("B", 1m, 20_000, 10, 20_000),
            new QuotePdfLine("C", 1m, 5_000, 21, 5_000)
        ]);

        var lines = Render(model).Text.Split('\n');

        Assert.Contains("Tipo de IVA Base Cuota", lines);
        Assert.Contains("10 % 200,00 € 20,00 €", lines);
        Assert.Contains("21 % 150,00 € 31,50 €", lines);
        Assert.Contains("Base imponible 350,00 €", lines);
        Assert.Contains("IVA 51,50 €", lines);
        Assert.Contains("TOTAL 401,50 €", lines);
    }

    [Fact]
    public void ItPrintsTheNotesAndKeepsTheirLineBreaks()
    {
        var lines = Render(PdfTestData.Model(notes: "Primera condición.\nSegunda condición.\n\nTercera, tras un hueco.")).Text.Split('\n');

        Assert.Contains("Condiciones y observaciones", lines);
        var first = Array.IndexOf(lines, "Primera condición.");
        Assert.True(first >= 0);
        Assert.Equal("Segunda condición.", lines[first + 1]);
        Assert.Equal("Tercera, tras un hueco.", lines[first + 2]);
    }

    [Fact]
    public void WithoutOptionalDataNothingIsPrintedForThem()
    {
        var issuer = PdfTestData.Issuer with { Province = null, Phone = null, Email = null };
        var model = PdfTestData.Model(issuer: issuer, notes: null, customer: new QuotePdfCustomer("Cliente Sin Dirección", null)) with { ValidUntil = null };

        var text = Render(model).Text;

        Assert.Contains("28009 Madrid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tel.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Válido hasta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Condiciones", text, StringComparison.Ordinal);
        Assert.Contains("Cliente Sin Dirección", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteWithoutLinesStillGivesADocumentWithZeroTotals()
    {
        var model = PdfTestData.Model(lines: [], notes: null);

        var lines = Render(model).Text.Split('\n');

        Assert.Contains("Descripción Cant. Precio IVA Importe", lines);
        Assert.Contains("TOTAL 0,00 €", lines);
    }

    // ---- Los importes son los del servidor (R5), con los casos fijos de la spec 023 -----------------------------------------------------------------------------------------------------------

    private sealed record VectorLine(decimal Quantity, long UnitPriceCents, int VatPercent, int Repeat);

    private sealed record VectorBreakdown(int VatPercent, long BaseCents, long VatCents);

    private sealed record Vector(string Name, List<VectorLine> Lines, long Net, long Vat, long Total, List<VectorBreakdown> Breakdown);

    public static TheoryData<int> VectorIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < LoadVectors().Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static List<Vector> LoadVectors()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Documents", "QuoteTotalsVectors.json"));
        using var document = JsonDocument.Parse(json);

        return document.RootElement.EnumerateArray().Select(e => new Vector(
            e.GetProperty("name").GetString()!,
            e.GetProperty("lines").EnumerateArray().Select(l => new VectorLine(l.GetProperty("quantity").GetDecimal(), l.GetProperty("unitPriceCents").GetInt64(), l.GetProperty("vatPercent").GetInt32(), l.TryGetProperty("repeat", out var repeat) ? repeat.GetInt32() : 1)).ToList(),
            e.GetProperty("net").GetInt64(),
            e.GetProperty("vat").GetInt64(),
            e.GetProperty("total").GetInt64(),
            e.GetProperty("breakdown").EnumerateArray().Select(b => new VectorBreakdown(b.GetProperty("vatPercent").GetInt32(), b.GetProperty("baseCents").GetInt64(), b.GetProperty("vatCents").GetInt64())).ToList())).ToList();
    }

    [Fact]
    public void TheFixedVectorsAreTheOnesOfTheSpec023()
    {
        // Control: si el fichero se vaciara o se acortara, el test de cada caso pasaría sobre nada.
        Assert.Equal(15, LoadVectors().Count);
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void ThePdfPrintsExactlyTheAmountsOfTheApiForEachFixedVector(int index)
    {
        var vector = LoadVectors()[index];
        var items = vector.Lines.SelectMany(l => Enumerable.Repeat(l, l.Repeat)).Select((l, i) => new QuoteItemInput("Línea " + (i + 1), l.Quantity, l.UnitPriceCents, l.VatPercent)).ToList();
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), null, 12, "Caso fijo", null, null, items);
        var dto = QuoteDto.From(quote, "Cliente de prueba");
        var company = new OfiFlow.Application.Company.CompanyProfileDto("Empresa S.L.", "B84531946", "Calle 1", "28009", "Madrid", null, null, null, IsComplete: true);

        // Lo que el servidor calcula es lo que dicen los casos fijos...
        Assert.Equal((vector.Net, vector.Vat, vector.Total), (dto.NetCents, dto.VatCents, dto.TotalCents));

        var lines = Render(QuotePdfModelFactory.Create(dto, company, null)).Text.Split('\n');

        // ...y el PDF imprime exactamente eso, línea a línea.
        Assert.Contains("Base imponible " + Money(vector.Net), lines);
        Assert.Contains("IVA " + Money(vector.Vat), lines);
        Assert.Contains("TOTAL " + Money(vector.Total), lines);
        foreach (var b in vector.Breakdown)
        {
            Assert.Contains($"{b.VatPercent} % {Money(b.BaseCents)} {Money(b.VatCents)}", lines);
        }

        foreach (var item in dto.Items)
        {
            Assert.Contains($"Línea {item.Position + 1} {SpanishFormat.Quantity(item.Quantity)} {Money(item.UnitPriceCents)} {item.VatPercent} % {Money(item.NetCents)}", lines);
        }
    }

    [Fact]
    public void ThePdfNeverRecalculatesAnythingItPrintsWhatTheModelSays()
    {
        // Un modelo deliberadamente incoherente: si el generador sumara, saldría otra cifra.
        var model = PdfTestData.Model() with { NetCents = 111, VatCents = 222, TotalCents = 400 };

        var lines = Render(model).Text.Split('\n');

        Assert.Contains("Base imponible 1,11 €", lines);
        Assert.Contains("IVA 2,22 €", lines);
        Assert.Contains("TOTAL 4,00 €", lines);
    }

    [Fact]
    public void TheLargestAmountsPrintWithoutLosingACentOrLeavingThePage()
    {
        var big = new QuotePdfLine("Extremo", 99_999.999m, 9_999_999_999, 21, 999_999_999_900_000);
        var model = PdfTestData.Model(lines: [big, big, big]);

        var pdf = Render(model);

        Assert.Contains("Extremo 99.999,999 99.999.999,99 € 21 % 9.999.999.999.000,00 €", pdf.Text.Split('\n'));
        AssertEverythingInsideThePage(pdf.Bytes);
    }

    // ---- La marca de borrador (R2) -------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static List<QuotePdfLine> ManyLines(int count) =>
        Enumerable.Range(1, count).Select(i => new QuotePdfLine($"Partida número {i} con una descripción larga para ocupar espacio en la tabla", 1m + i % 3, 1_000 + i, 21, (1_000 + i) * (1 + i % 3))).ToList();

    [Fact]
    public void ADraftCarriesTheMarkOnEveryPageAndASentQuoteOnNone()
    {
        var draft = Render(PdfTestData.Model(isDraft: true, lines: ManyLines(100)));
        var sent = Render(PdfTestData.Model(isDraft: false, lines: ManyLines(100)));

        Assert.True(draft.PageCount > 1);
        Assert.All(draft.Pages, page => Assert.Contains("BORRADOR", page, StringComparison.Ordinal));
        Assert.Equal(sent.PageCount, draft.PageCount);
        Assert.All(sent.Pages, page => Assert.DoesNotContain("BORRADOR", page, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDraftMarkIsOnTheSinglePageOfASmallDraft()
    {
        Assert.Contains("BORRADOR", Render(PdfTestData.Model(isDraft: true)).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BORRADOR", Render(PdfTestData.Model(isDraft: false)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDraftMarkIsDiagonalAndSemiTransparent()
    {
        using var document = PigDocument.Open(Renderer.Render(PdfTestData.Model(isDraft: true)));
        var mark = document.GetPage(1).Letters.Where(l => l.PointSize > 60).ToList();

        Assert.Equal(8, mark.Count);
        // Rotada 45°: ninguna letra está en horizontal con la anterior.
        Assert.True(mark.Select(l => Math.Round(l.GlyphRectangle.BottomLeft.Y)).Distinct().Count() > 4);
    }

    // ---- Varias páginas (R8) ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void HundredLinesGoOverSeveralPagesInOrderWithEveryDescription()
    {
        var pdf = Render(PdfTestData.Model(lines: ManyLines(100)));

        Assert.True(pdf.PageCount >= 3);
        var descriptions = pdf.Text.Split('\n').Where(l => l.StartsWith("Partida número ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[2], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(1, 100), descriptions);
    }

    [Fact]
    public void TheHeaderOfTheLinesTableRepeatsOnEveryPageThatHasLines()
    {
        var pdf = Render(PdfTestData.Model(lines: ManyLines(100)));

        foreach (var page in pdf.Pages.Where(p => p.Contains("Partida número", StringComparison.Ordinal)))
        {
            Assert.Contains("Descripción Cant. Precio IVA Importe", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePageNumbersAreInTheFooterOfEachPage()
    {
        var pdf = Render(PdfTestData.Model(lines: ManyLines(100)));

        for (var i = 0; i < pdf.PageCount; i++)
        {
            Assert.Contains($"P-0012 · Página {i + 1} de {pdf.PageCount}", pdf.Pages[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheTotalsAppearOnceAndNeverSplitBetweenTwoPagesWhateverTheNumberOfLines()
    {
        // Se prueba con cada cantidad de líneas de 0 a 70: en alguna de ellas el desglose caería justo en el borde de una página.
        for (var count = 0; count <= 70; count++)
        {
            var pdf = Render(PdfTestData.Model(lines: ManyLines(count), notes: null));
            var withTotals = pdf.Pages.Where(p => p.Contains("TOTAL", StringComparison.Ordinal)).ToList();

            Assert.True(withTotals.Count == 1, $"Con {count} líneas, TOTAL sale en {withTotals.Count} páginas.");
            Assert.Contains("Tipo de IVA", withTotals[0], StringComparison.Ordinal);
            Assert.Contains("Base imponible", withTotals[0], StringComparison.Ordinal);
            Assert.Equal(1, CountOf(pdf.Text, "Tipo de IVA"));
            Assert.Equal(1, CountOf(pdf.Text, "Base imponible"));
        }
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ---- El texto libre es texto (R7) --------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FreeTextIsPrintedAsItIsWithoutInterpretingAnything()
    {
        var payloads = new[] { "<b>negrita</b>", "&amp; &lt;", "%s %d {0}", "\\n \\\\", "\"comillas\"", "[x](http://a.test)", "=SUMA(A1)", "${x}", "<script>x</script>" };
        var model = PdfTestData.Model(
            title: string.Join(' ', payloads.Take(4)),
            lines: [new QuotePdfLine(string.Join(' ', payloads.Skip(4).Take(3)), 1m, 100, 21, 100), new QuotePdfLine(string.Join(' ', payloads.Skip(7)), 1m, 100, 21, 100)],
            notes: string.Join('\n', payloads),
            customer: new QuotePdfCustomer(payloads[0], payloads[1]));

        var text = Squash(Render(model).Text);

        foreach (var payload in payloads)
        {
            Assert.Contains(Squash(payload), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACharacterTheFontDoesNotHaveIsPrintedAsAQuestionMarkAndNothingBreaks()
    {
        var model = PdfTestData.Model(
            title: "Reforma " + C(0x1F600) + " cocina",
            lines: [new QuotePdfLine("Pintura " + C(0x4E2D) + C(0x6587), 1m, 100, 21, 100)],
            customer: new QuotePdfCustomer("Cliente " + C(0x645) + C(0x631), null));

        var text = Render(model).Text;

        Assert.Contains("Reforma ? cocina", text, StringComparison.Ordinal);
        Assert.Contains("Pintura ??", text, StringComparison.Ordinal);
        Assert.Contains("Cliente ??", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlAndDirectionCharactersDoNotReachTheDocument()
    {
        var model = PdfTestData.Model(title: "Pago" + C(0x202E) + "fdp.exe" + C(0) + C(7) + C(0x200B) + "fin", notes: "uno" + C(0x202E) + "dos" + C(0x200B) + "tres");

        var text = Render(model).Text;

        Assert.Contains("Pagofdp.exefin", text, StringComparison.Ordinal);
        Assert.Contains("unodostres", text, StringComparison.Ordinal);
        Assert.DoesNotContain(C(0x202E), text, StringComparison.Ordinal);
        Assert.DoesNotContain(C(0x200B), text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFiveHundredLetterWordKeepsEveryLetterAndStaysInsideThePage()
    {
        var word = string.Concat(Enumerable.Range(0, 500).Select(i => (char)('A' + i % 26)));
        var model = PdfTestData.Model(
            title: word[..200],
            lines: [new QuotePdfLine(word, 1m, 100, 21, 100)],
            notes: word + " " + word,
            customer: new QuotePdfCustomer(word[..200], word),
            issuer: PdfTestData.Issuer with { LegalName = word[..200], AddressLine = word[..200], Email = word[..200] });

        var pdf = Render(model);

        Assert.Contains(word, Squash(pdf.Text), StringComparison.Ordinal);
        AssertEverythingInsideThePage(pdf.Bytes);
    }

    [Fact]
    public void WideLettersInALongWordStayInsideThePageToo()
    {
        // La «W» es de las letras más anchas: el tope de caracteres seguidos tiene que valer también para ella.
        var wide = new string('W', 400);
        var model = PdfTestData.Model(title: wide[..200], lines: [new QuotePdfLine(wide, 1m, 100, 21, 100)], notes: wide, customer: new QuotePdfCustomer(wide[..200], wide));

        AssertEverythingInsideThePage(Renderer.Render(model));
    }

    [Fact]
    public void EveryFieldAtItsMaximumLengthGivesADocumentInsideThePage()
    {
        var sentence = string.Concat(Enumerable.Repeat("texto largo de prueba con muchas palabras normales ", 60));
        var lines = Enumerable.Range(1, 100).Select(i => new QuotePdfLine(sentence[..500], 99_999.999m, 9_999_999_999, 21, 999_999_999_900_000)).ToList();
        var model = PdfTestData.Model(
            title: sentence[..200],
            lines: lines,
            notes: sentence[..2000],
            customer: new QuotePdfCustomer(sentence[..200], sentence[..500]),
            issuer: new QuotePdfIssuer(sentence[..200], "B84531946", sentence[..200], "28009", sentence[..100], sentence[..100], "912345678", sentence[..100]));

        var pdf = Render(model);

        Assert.True(pdf.PageCount > 5);
        AssertEverythingInsideThePage(pdf.Bytes);
    }

    [Fact]
    public void ManyBlankLinesInTheNotesDoNotMakeEmptyPages()
    {
        var pdf = Render(PdfTestData.Model(notes: "inicio" + new string('\n', 2000) + "fin"));

        Assert.Equal(1, pdf.PageCount);
        Assert.Contains("inicio", pdf.Text, StringComparison.Ordinal);
        Assert.Contains("fin", pdf.Text, StringComparison.Ordinal);
    }

    // ---- Un PDF inerte (R9) y sin datos de la máquina ---------------------------------------------------------------------------------------------------------------------------------------

    // Las claves de un diccionario PDF que ejecutan algo, enlazan, adjuntan o piden datos. Ninguna puede aparecer en ningún objeto del documento.
    private static readonly string[] ForbiddenKeys =
    [
        "/JavaScript", "/JS", "/OpenAction", "/AA", "/Launch", "/URI", "/EmbeddedFile", "/EmbeddedFiles", "/AcroForm", "/Annots", "/Names", "/AF", "/RichMedia", "/XFA", "/SubmitForm", "/GoToR", "/GoToE", "/ImportData", "/Encrypt"
    ];

    [Fact]
    public void NoObjectOfTheDocumentHasAKeyThatExecutesLinksAttachesOrAsksForData()
    {
        foreach (var model in new[] { PdfTestData.Model(), PdfTestData.Model(isDraft: true, lines: ManyLines(60)) })
        {
            using var stream = new MemoryStream(Renderer.Render(model));
            using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            var objects = document.Internals.GetAllObjects();

            Assert.NotEmpty(objects);
            foreach (var obj in objects)
            {
                if (obj is PdfDictionary dictionary)
                {
                    var found = dictionary.Elements.Keys.Where(k => ForbiddenKeys.Contains(k, StringComparer.Ordinal)).ToList();
                    Assert.True(found.Count == 0, $"Un objeto del PDF tiene la clave {string.Join(", ", found)}.");
                }
            }

            var catalogKeys = document.Internals.Catalog.Elements.Keys.ToList();
            Assert.All(catalogKeys, key => Assert.Contains(key, new[] { "/Type", "/Pages", "/Version", "/Metadata" }));
        }
    }

    [Fact]
    public void TheInertCheckWouldCatchALinkSoItProvesSomething()
    {
        // Control positivo: un PDF con un enlace SÍ tiene una anotación, y la comprobación anterior lo vería.
        using var linked = new SharpDocument();
        var page = linked.AddPage();
        page.AddWebLink(new PdfSharp.Pdf.PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, 100, 20)), "https://example.test");
        using var stream = new MemoryStream();
        linked.Save(stream, closeStream: false);
        stream.Position = 0;
        using var reopened = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        var keys = reopened.Internals.GetAllObjects().OfType<PdfDictionary>().SelectMany(d => d.Elements.Keys).ToList();

        Assert.Contains(keys, k => ForbiddenKeys.Contains(k, StringComparer.Ordinal));
    }

    [Fact]
    public void TheMetadataSayOnlyOfiFlowTheTitleAndTheLibrary()
    {
        var pdf = Render(PdfTestData.Model());

        Assert.Equal("Presupuesto P-0012", pdf.Title);
        Assert.Equal("OfiFlow", pdf.Creator);
        Assert.True(string.IsNullOrEmpty(pdf.Author), $"Autor: «{pdf.Author}»");
        Assert.True(string.IsNullOrEmpty(pdf.Subject));
        Assert.True(string.IsNullOrEmpty(pdf.Keywords));
        // PDFsharp escribe siempre su nombre y su versión como productor: la librería, no la máquina.
        Assert.StartsWith("PDFsharp ", pdf.Producer, StringComparison.Ordinal);
        Assert.DoesNotContain("Original", pdf.Producer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentDoesNotContainTheUserTheMachineOrAPathOfThisComputer()
    {
        var bytes = Renderer.Render(PdfTestData.Model());
        var raw = Encoding.Latin1.GetString(bytes);

        foreach (var forbidden in new[] { Environment.UserName, Environment.MachineName, AppContext.BaseDirectory, "GestionRicarma" })
        {
            if (forbidden.Length >= 5)
            {
                Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheTextOfThePdfIsEmbeddedInTheFontsOfTheRepositoryAndNotInTheOnesOfTheMachine()
    {
        using var document = PigDocument.Open(Renderer.Render(PdfTestData.Model(isDraft: true)));
        var fonts = document.GetPages().SelectMany(p => p.Letters).Select(l => l.FontName).Distinct().ToList();

        Assert.NotEmpty(fonts);
        Assert.All(fonts, name => Assert.Contains("Liberation Sans", name!, StringComparison.Ordinal));
    }

    // ---- Mismo dato, mismo documento (R10), cultura y hilos ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TwoRendersOfTheSameModelHaveTheSameContent()
    {
        var model = PdfTestData.Model(lines: ManyLines(80), isDraft: true);

        var first = Render(model);
        var second = Render(model);

        Assert.Equal(first.PageCount, second.PageCount);
        Assert.Equal(first.Pages, second.Pages);
    }

    [Fact]
    public void TheCultureOfTheServerDoesNotChangeTheDocument()
    {
        var model = PdfTestData.Model(lines: [new QuotePdfLine("Cable", 1234.567m, 123_456, 21, 152_222)]);
        var expected = Render(model).Pages;
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "ar-SA", "de-DE", "en-US", "th-TH" })
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
                Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);

                Assert.Equal(expected, Render(model).Pages);
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
            Thread.CurrentThread.CurrentUICulture = original;
        }
    }

    [Fact]
    public void SeveralThreadsCanGenerateAtTheSameTime()
    {
        var results = new ReadPdf[16];

        Parallel.For(0, results.Length, i => results[i] = Render(PdfTestData.Model(isDraft: i % 2 == 0, lines: ManyLines(10 + i))));

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Contains("TOTAL", results[i].Text, StringComparison.Ordinal);
            Assert.Equal(i % 2 == 0, results[i].Text.Contains("BORRADOR", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ARenderOfTheBiggestDocumentIsQuick()
    {
        var model = PdfTestData.Model(lines: ManyLines(100), notes: new string('x', 20) + string.Concat(Enumerable.Repeat(" palabra", 250)), isDraft: true);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Renderer.Render(model);

        // Una cota generosa (un PDF así tarda décimas de segundo): solo salta si la generación se vuelve cuadrática o se cuelga.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {clock.Elapsed}.");
    }

    [Fact]
    public void ANullModelIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Renderer.Render(null!));
    }

    // ---- Nada se sale de la página -------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static void AssertEverythingInsideThePage(byte[] bytes)
    {
        const double margin = 2.0 / 2.54 * 72 - 1.5; // 2 cm en puntos, con un poco de tolerancia por el hueco lateral de cada glifo
        using var document = PigDocument.Open(bytes);

        foreach (var page in document.GetPages())
        {
            foreach (var letter in page.Letters.Where(l => l.PointSize < 60))
            {
                var box = letter.GlyphRectangle;
                Assert.True(box.Left >= margin, $"Página {page.Number}: «{letter.Value}» empieza en x={box.Left:F1}, antes del margen.");
                Assert.True(box.Right <= page.Width - margin, $"Página {page.Number}: «{letter.Value}» termina en x={box.Right:F1}, pasado el margen derecho ({page.Width - margin:F1}).");
                Assert.True(box.Bottom >= 0 && box.Top <= page.Height, $"Página {page.Number}: «{letter.Value}» se sale por arriba o por abajo.");
            }
        }
    }
}

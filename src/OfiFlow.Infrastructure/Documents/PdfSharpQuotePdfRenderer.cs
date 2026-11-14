using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using OfiFlow.Application.Quotes.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OfiFlow.Infrastructure.Documents;

/// <summary>
/// Maqueta el PDF de un presupuesto con PDFsharp + MigraDoc (spec 025, ADR-028). <b>Solo imprime</b>: recibe un <see cref="QuotePdfModel"/> con todo ya calculado y no suma, no multiplica ni redondea
/// (R5). El texto libre pasa por <see cref="PdfText"/> y se imprime como texto, sin interpretar nada (R7); los importes y fechas, por <see cref="MoneyFormat"/> y <see cref="SpanishFormat"/> (R6).
/// El documento es inerte (R9): no se añade ningún enlace, script, adjunto ni formulario, y los metadatos son solo «OfiFlow» y el título.
/// </summary>
public sealed class PdfSharpQuotePdfRenderer : IQuotePdfRenderer
{
    // La página A4: 21 cm de ancho menos dos márgenes de 2 cm = 17 cm de texto.
    private const double MarginCm = 2.0;
    private const double PointsPerCm = 72.0 / 2.54;
    private const double TextWidth = 17.0 * PointsPerCm;

    // El relleno lateral que MigraDoc pone por defecto a cada celda (0,19 cm por lado): lo que cabe dentro de una columna es su ancho menos esto.
    private const double CellPadding = 2 * 0.19 * PointsPerCm;

    // Un pequeño margen para que ninguna letra toque el borde de su hueco.
    private const double Safety = 0.97;

    private const string Gray = "#595959";

    // Las columnas de la tabla de líneas (suman 17 cm). Los importes ocupan lo que necesita el mayor posible (spec 023: 99 999,999 × 99 999 999,99 €).
    private const double DescriptionCm = 5.7;
    private const double QuantityCm = 2.2;
    private const double UnitPriceCm = 3.2;
    private const double VatCm = 1.5;
    private const double NetCm = 4.4;

    private const double HeaderLeftCm = 9.5;
    private const double HeaderRightCm = 7.5;

    public byte[] Render(QuotePdfModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        PdfFonts.EnsureRegistered();

        Document document;
        using (var meter = new TextMeter())
        {
            document = BuildDocument(model, meter);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        var pdf = renderer.PdfDocument;
        pdf.Info.Creator = "OfiFlow";

        if (model.IsDraft)
        {
            foreach (var page in pdf.Pages)
            {
                DrawDraftMark(page);
            }
        }

        using var output = new MemoryStream();
        pdf.Save(output, closeStream: false);

        return output.ToArray();
    }

    private static Document BuildDocument(QuotePdfModel model, TextMeter meter)
    {
        var document = new Document();
        document.Info.Title = "Presupuesto " + model.Label;

        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfFonts.FamilyName;
        normal.Font.Size = 9;
        normal.ParagraphFormat.SpaceAfter = 0;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Portrait;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(MarginCm);
        section.PageSetup.RightMargin = Unit.FromCentimeter(MarginCm);
        section.PageSetup.TopMargin = Unit.FromCentimeter(MarginCm);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.2);

        AddFooter(section, model);
        AddHeader(section, model, meter);
        AddCustomer(section, model, meter);
        AddTitle(section, model, meter);
        AddLines(section, model, meter);
        AddTotals(section, model);
        AddNotes(section, model, meter);

        return document;
    }

    private static void AddFooter(Section section, QuotePdfModel model)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = Color.Parse(Gray);
        footer.AddText($"{model.Label} · Página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();
    }

    private static void AddHeader(Section section, QuotePdfModel model, TextMeter meter)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn(Unit.FromCentimeter(HeaderLeftCm));
        table.AddColumn(Unit.FromCentimeter(HeaderRightCm));

        var row = table.AddRow();
        var inner = HeaderLeftCm * PointsPerCm - CellPadding;

        // Quien emite.
        var issuer = row.Cells[0];
        var name = issuer.AddParagraph();
        name.Format.Font.Size = 12;
        name.Format.Font.Bold = true;
        AddMultiline(name, Fit(model.Issuer.LegalName, allowNewlines: false, meter, bold: true, size: 12, inner));
        AddLine(issuer, Fit("NIF " + model.Issuer.TaxId, false, meter, false, 9, inner));
        AddLine(issuer, Fit(model.Issuer.AddressLine, false, meter, false, 9, inner));
        var place = model.Issuer.PostalCode + " " + model.Issuer.City;
        if (PdfText.Clean(model.Issuer.Province, allowNewlines: false) is { Length: > 0 } province)
        {
            place += " (" + province + ")";
        }

        AddLine(issuer, Fit(place, false, meter, false, 9, inner));
        if (PdfText.Clean(model.Issuer.Phone, allowNewlines: false) is { Length: > 0 } phone)
        {
            AddLine(issuer, Fit("Tel. " + phone, false, meter, false, 9, inner));
        }

        if (PdfText.Clean(model.Issuer.Email, allowNewlines: false) is { Length: > 0 } email)
        {
            AddLine(issuer, Fit(email, false, meter, false, 9, inner));
        }

        // El documento.
        var info = row.Cells[1];
        info.Format.Alignment = ParagraphAlignment.Right;
        var heading = info.AddParagraph("PRESUPUESTO");
        heading.Format.Font.Size = 16;
        heading.Format.Font.Bold = true;
        var number = info.AddParagraph(model.Label);
        number.Format.Font.Size = 12;
        number.Format.Font.Bold = true;
        AddLine(info, "Fecha: " + SpanishFormat.Date(model.IssueDate));
        if (model.ValidUntil is { } validUntil)
        {
            AddLine(info, "Válido hasta: " + SpanishFormat.Date(validUntil));
        }
    }

    private static void AddCustomer(Section section, QuotePdfModel model, TextMeter meter)
    {
        var label = section.AddParagraph("Cliente");
        label.Format.SpaceBefore = Unit.FromCentimeter(0.8);
        label.Format.Font.Size = 8;
        label.Format.Font.Color = Color.Parse(Gray);

        var name = section.AddParagraph();
        name.Format.Font.Size = 11;
        name.Format.Font.Bold = true;
        AddMultiline(name, Fit(model.Customer.Name, allowNewlines: false, meter, bold: true, size: 11, TextWidth));

        if (Fit(model.Customer.Address, allowNewlines: true, meter, bold: false, size: 9, TextWidth) is { Length: > 0 } address)
        {
            AddMultiline(section.AddParagraph(), address);
        }
    }

    private static void AddTitle(Section section, QuotePdfModel model, TextMeter meter)
    {
        var title = section.AddParagraph();
        title.Format.SpaceBefore = Unit.FromCentimeter(0.7);
        title.Format.SpaceAfter = Unit.FromCentimeter(0.3);
        title.Format.Font.Size = 12;
        title.Format.Font.Bold = true;
        AddMultiline(title, Fit(model.Title, allowNewlines: false, meter, bold: true, size: 12, TextWidth));
    }

    private static void AddLines(Section section, QuotePdfModel model, TextMeter meter)
    {
        var table = section.AddTable();
        table.Borders.Color = Color.Parse("#BFBFBF");
        table.Borders.Width = 0.5;
        table.Format.Font.Size = 9;
        table.TopPadding = 2;
        table.BottomPadding = 2;

        table.AddColumn(Unit.FromCentimeter(DescriptionCm));
        table.AddColumn(Unit.FromCentimeter(QuantityCm));
        table.AddColumn(Unit.FromCentimeter(UnitPriceCm));
        table.AddColumn(Unit.FromCentimeter(VatCm));
        table.AddColumn(Unit.FromCentimeter(NetCm));

        // La cabecera se repite en cada página (R8).
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Color.Parse("#EFEFEF");
        header.Format.Font.Bold = true;
        header.Cells[0].AddParagraph("Descripción");
        SetRight(header.Cells[1], "Cant.");
        SetRight(header.Cells[2], "Precio");
        SetRight(header.Cells[3], "IVA");
        SetRight(header.Cells[4], "Importe");

        var descriptionWidth = DescriptionCm * PointsPerCm - CellPadding;
        foreach (var line in model.Lines)
        {
            var row = table.AddRow();
            row.Cells[0].Format.Alignment = ParagraphAlignment.Left;
            AddMultiline(row.Cells[0].AddParagraph(), Fit(line.Description, allowNewlines: false, meter, bold: false, size: 9, descriptionWidth));
            SetRight(row.Cells[1], SpanishFormat.Quantity(line.Quantity));
            SetRight(row.Cells[2], MoneyFormat.Format(line.UnitPriceCents));
            SetRight(row.Cells[3], SpanishFormat.Percent(line.VatPercent));
            SetRight(row.Cells[4], MoneyFormat.Format(line.NetCents));
        }
    }

    private static void AddTotals(Section section, QuotePdfModel model)
    {
        var spacer = section.AddParagraph();
        spacer.Format.SpaceBefore = Unit.FromCentimeter(0.4);

        // El desglose por tipo de IVA y los totales, en una sola tabla que no se parte entre dos páginas (R8).
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.Format.Font.Size = 9;
        table.TopPadding = 1.5;
        table.BottomPadding = 1.5;
        table.Rows.LeftIndent = Unit.FromCentimeter(6.0);
        table.AddColumn(Unit.FromCentimeter(2.6));
        table.AddColumn(Unit.FromCentimeter(4.2));
        table.AddColumn(Unit.FromCentimeter(4.2));

        var header = table.AddRow();
        header.Shading.Color = Color.Parse("#EFEFEF");
        header.Format.Font.Bold = true;
        header.Cells[0].AddParagraph("Tipo de IVA");
        SetRight(header.Cells[1], "Base");
        SetRight(header.Cells[2], "Cuota");

        foreach (var vat in model.Breakdown)
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(SpanishFormat.Percent(vat.VatPercent));
            SetRight(row.Cells[1], MoneyFormat.Format(vat.BaseCents));
            SetRight(row.Cells[2], MoneyFormat.Format(vat.VatCents));
        }

        AddTotalRow(table, "Base imponible", model.NetCents, bold: false);
        AddTotalRow(table, "IVA", model.VatCents, bold: false);
        AddTotalRow(table, "TOTAL", model.TotalCents, bold: true);

        // La primera fila se mantiene con todas las demás: o caben todas en la página o pasan todas a la siguiente.
        table.Rows[0].KeepWith = table.Rows.Count - 1;
    }

    private static void AddTotalRow(Table table, string label, long cents, bool bold)
    {
        var row = table.AddRow();
        row.Format.Font.Bold = bold;
        if (bold)
        {
            row.Format.Font.Size = 11;
            row.Borders.Top.Width = 0.75;
            row.Borders.Top.Color = Color.Parse(Gray);
        }

        row.Cells[0].MergeRight = 1;
        row.Cells[0].AddParagraph(label);
        SetRight(row.Cells[2], MoneyFormat.Format(cents));
    }

    private static void AddNotes(Section section, QuotePdfModel model, TextMeter meter)
    {
        if (Fit(model.Notes, allowNewlines: true, meter, bold: false, size: 9, TextWidth) is not { Length: > 0 } notes)
        {
            return;
        }

        var label = section.AddParagraph("Condiciones y observaciones");
        label.Format.SpaceBefore = Unit.FromCentimeter(0.8);
        label.Format.SpaceAfter = Unit.FromCentimeter(0.1);
        label.Format.Font.Bold = true;
        label.Format.KeepWithNext = true;

        AddMultiline(section.AddParagraph(), notes);
    }

    /// <summary>La marca «BORRADOR» en diagonal, bajo el contenido y con transparencia, en <b>cada</b> página (R2).</summary>
    private static void DrawDraftMark(PdfPage page)
    {
        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
        var font = new XFont(PdfFonts.FamilyName, 96, XFontStyleEx.Bold);
        var brush = new XSolidBrush(XColor.FromArgb(48, 192, 0, 0));

        graphics.TranslateTransform(page.Width.Point / 2, page.Height.Point / 2);
        graphics.RotateTransform(-45);
        graphics.DrawString("BORRADOR", font, brush, new XPoint(0, 0), XStringFormats.Center);
    }

    /// <summary>
    /// El texto libre listo para imprimir en un hueco de <paramref name="width"/> puntos: limpio (<see cref="PdfText.Clean"/>) y con las palabras que no caben partidas donde dejan de caber
    /// (<see cref="PdfText.BreakLongWords"/>), midiendo cada letra con la fuente y el cuerpo con que se va a dibujar.
    /// </summary>
    private static string Fit(string? text, bool allowNewlines, TextMeter meter, bool bold, double size, double width) =>
        PdfText.BreakLongWords(PdfText.Clean(text, allowNewlines), width * Safety, meter.For(bold, size));

    private static void AddLine(Cell cell, string text) => AddMultiline(cell.AddParagraph(), text);

    private static void SetRight(Cell cell, string text)
    {
        var paragraph = cell.AddParagraph();
        paragraph.Format.Alignment = ParagraphAlignment.Right;
        paragraph.AddText(text);
    }

    private static void AddMultiline(Paragraph paragraph, string text)
    {
        // Siempre texto: AddText no interpreta nada (ni formato ni enlaces).
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.AddLineBreak();
            }

            if (lines[i].Length > 0)
            {
                paragraph.AddText(lines[i]);
            }
        }
    }

    /// <summary>Mide el ancho de cada letra con la fuente incluida, sin dibujar nada: un contexto de medida de PDFsharp y una caché por letra, cuerpo y negrita.</summary>
    private sealed class TextMeter : IDisposable
    {
        private readonly XGraphics _graphics = XGraphics.CreateMeasureContext(new XSize(595, 842), XGraphicsUnit.Point, XPageDirection.Downwards);
        private readonly Dictionary<(bool Bold, double Size), (XFont Font, Dictionary<char, double> Widths)> _fonts = [];

        public Func<char, double> For(bool bold, double size)
        {
            if (!_fonts.TryGetValue((bold, size), out var entry))
            {
                entry = (new XFont(PdfFonts.FamilyName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular), []);
                _fonts[(bold, size)] = entry;
            }

            return c =>
            {
                if (!entry.Widths.TryGetValue(c, out var width))
                {
                    width = _graphics.MeasureString(c.ToString(), entry.Font).Width;
                    entry.Widths[c] = width;
                }

                return width;
            };
        }

        public void Dispose() => _graphics.Dispose();
    }
}

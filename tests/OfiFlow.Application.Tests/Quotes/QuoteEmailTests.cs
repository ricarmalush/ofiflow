using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// El correo de un presupuesto (spec 026, R11, R13, R14, D9, D10): una función pura, así que se prueba con tablas de casos. Lo que importa: <b>nada que escriba la persona entra en el
/// asunto ni en el cuerpo</b>, y el nombre de la empresa (que sí entra) sale en una sola línea.
/// </summary>
public class QuoteEmailTests
{
    private static readonly Email To = Email.Create("cliente@ejemplo.es");
    private static readonly Email CompanyEmail = Email.Create("hola@fontaneria.example");
    private static readonly QuotePdfFile Pdf = new("presupuesto-P-0012.pdf", "%PDF-1.7 prueba"u8.ToArray());

    private static EmailMessage Build(
        string company = "Fontanería Pérez S.L.", long total = 123_456, DateOnly? validUntil = null, Email? companyEmail = null, int number = 12) =>
        QuoteEmail.Build(To, number, company, total, validUntil, companyEmail, Pdf);

    [Theory]
    [InlineData(1, "Presupuesto P-0001")]
    [InlineData(12, "Presupuesto P-0012")]
    [InlineData(12345, "Presupuesto P-12345")]
    public void TheSubject_IsOnlyTheQuoteNumber(int number, string expected) => Assert.Equal(expected, Build(number: number).Subject);

    [Fact]
    public void TheSubject_CarriesNoCompanyAndNoControlCharacters()
    {
        var message = Build(company: "Banco X\r\nBcc: otro@x.com");

        Assert.DoesNotContain("Banco", message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(message.Subject, char.IsControl);
    }

    [Fact]
    public void TheMessage_IsAddressedToTheGivenEmail() => Assert.Equal(To, Build().To);

    [Fact]
    public void TheBody_SaysWhoSendsIt_TheNumber_AndTheTotal_InSpanishFormat()
    {
        var body = Build(total: 123_456).TextBody;

        Assert.Contains("Fontanería Pérez S.L. te envía el presupuesto P-0012", body, StringComparison.Ordinal);
        Assert.Contains("1.234,56\u00A0€ (IVA incluido)", body, StringComparison.Ordinal);
        Assert.Contains("PDF adjunto", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidity_IsInTheBodyOnlyWhenThereIsOne_WithAFixedFormat()
    {
        Assert.Contains("Es válido hasta el 31/12/2026.", Build(validUntil: new DateOnly(2026, 12, 31)).TextBody, StringComparison.Ordinal);
        Assert.Contains("Es válido hasta el 05/01/2027.", Build(validUntil: new DateOnly(2027, 1, 5)).TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("válido hasta", Build(validUntil: null).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidityFormat_DoesNotDependOnTheServerCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // El alemán usa el punto como separador de fechas: si el formato dependiera de la cultura del servidor, saldría «31.12.2026».
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

            Assert.Contains("Es válido hasta el 31/12/2026.", Build(validUntil: new DateOnly(2026, 12, 31)).TextBody, StringComparison.Ordinal);
            Assert.Contains("1.234,56 €", Build(total: 123_456).TextBody, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheAmount_IsPrintedFromTheGivenCents_WithoutRecalculating()
    {
        Assert.Contains("0,07\u00A0€", Build(total: 7).TextBody, StringComparison.Ordinal);
        Assert.Contains("99.999.999,99\u00A0€", Build(total: 9_999_999_999).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WithACompanyEmail_ItBecomesTheReplyTo_AndTheBodySaysSo()
    {
        var message = Build(companyEmail: CompanyEmail);

        Assert.Equal(CompanyEmail, message.ReplyTo);
        Assert.Contains("responde a este correo", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("hola@fontaneria.example", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACompanyEmail_ThereIsNoReplyTo_AndTheBodySendsYouToTheCompany()
    {
        var message = Build(companyEmail: null);

        Assert.Null(message.ReplyTo);
        Assert.Contains("contacta con Fontanería Pérez S.L.", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("responde a este correo", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePdf_GoesAttached_WithTheNumberOnlyName()
    {
        var attachment = Build().Attachment;

        Assert.NotNull(attachment);
        Assert.Equal("presupuesto-P-0012.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(Pdf.Content, attachment.Content);
    }

    [Theory]
    [InlineData("Fontanería\r\nBcc: otro@x.com", "Fontanería Bcc: otro@x.com")]
    [InlineData("  Fontanería \t  Pérez  ", "Fontanería Pérez")]
    [InlineData("A\u2028B\u2029C", "A B C")]
    [InlineData("Banco\u202Eoreivals", "Banco oreivals")]
    [InlineData("A\0B\u0007C", "A B C")]
    [InlineData("Normal", "Normal")]
    public void TheCompanyName_IsOneCleanLine(string raw, string expected) => Assert.Equal(expected, QuoteEmail.OneLine(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    [InlineData("\u202E")]
    public void ACompanyNameWithNothingVisible_BecomesAGenericOne(string raw) => Assert.Equal("La empresa", QuoteEmail.OneLine(raw));

    [Fact]
    public void AHostileCompanyName_AddsNoLineBreakAndNoControlCharacterToTheTemplate()
    {
        var body = Build(company: "X\r\nBcc: otro@x.com\r\n\r\nPaga aquí: http://malo.example\u202E").TextBody;

        // Las únicas rupturas de línea son las de la plantilla (CRLF): el nombre no añadió ninguna.
        var template = Build(company: "X").TextBody.Split("\r\n").Length;
        Assert.Equal(template, body.Split("\r\n").Length);
        Assert.DoesNotContain(body.Replace("\r\n", ""), c => char.IsControl(c) || c == '\u202E');
    }

    [Fact]
    public void NothingTheUserTypesInTheQuoteCanReachTheMessage()
    {
        // La función no recibe título, notas ni nombre de cliente: solo el nombre fiscal de la empresa. Este test fija que la firma pública no los admite.
        var parameters = typeof(QuoteEmail).GetMethod(nameof(QuoteEmail.Build))!.GetParameters().Select(p => p.Name!).ToArray();

        Assert.Equal(["to", "quoteNumber", "companyLegalName", "totalCents", "validUntil", "companyEmail", "pdf"], parameters);
    }

    [Fact]
    public void TheBody_UsesWindowsLineEndings_LikeTheOtherTemplates() => Assert.Contains("\r\n", Build().TextBody, StringComparison.Ordinal);
}

using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Tests.Common.Mail;

/// <summary>
/// El único adjunto que un correo puede llevar (spec 026, R14, D12): se valida al construirse, así que un adjunto que no cumple <b>no llega a existir</b>.
/// </summary>
public class EmailAttachmentTests
{
    private static readonly byte[] Pdf = "%PDF-1.7 prueba"u8.ToArray();

    [Fact]
    public void AValidPdf_IsAccepted_AndKeepsItsParts()
    {
        var attachment = new EmailAttachment("presupuesto-P-0012.pdf", "application/pdf", Pdf);

        Assert.Equal("presupuesto-P-0012.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(Pdf, attachment.Content);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("presupuesto-P-0012.pdf")]
    [InlineData("A_b-C.1.pdf")]
    public void ASafeFileName_IsAccepted(string name) =>
        Assert.Equal(name, new EmailAttachment(name, "application/pdf", Pdf).FileName);

    [Fact]
    public void AFileNameOfExactlyTheMaximumLength_IsAccepted() =>
        Assert.Equal(100, new EmailAttachment(new string('a', EmailAttachment.FileNameMaxLength), "application/pdf", Pdf).FileName.Length);

    [Theory]
    [InlineData("")]
    [InlineData("a/b.pdf")]
    [InlineData("a\\b.pdf")]
    [InlineData("..")]
    [InlineData("a..b.pdf")]
    [InlineData("../etc/passwd")]
    [InlineData("a\"b.pdf")]
    [InlineData("a b.pdf")]
    [InlineData("a;b.pdf")]
    [InlineData("a\r\nBcc: x@y.z.pdf")]
    [InlineData("a\0b.pdf")]
    [InlineData("presupuesto-ñ.pdf")]
    [InlineData("presupuesto-\u202E.pdf")]
    public void AnUnsafeFileName_IsRefused(string name) =>
        Assert.Throws<ArgumentException>(() => new EmailAttachment(name, "application/pdf", Pdf));

    [Fact]
    public void AFileNameOneCharacterOverTheMaximum_IsRefused() =>
        Assert.Throws<ArgumentException>(() => new EmailAttachment(new string('a', EmailAttachment.FileNameMaxLength + 1), "application/pdf", Pdf));

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    [InlineData("application/PDF")]
    [InlineData("application/pdf; name=x")]
    [InlineData("application/pdf\r\nX-Evil: 1")]
    [InlineData("")]
    public void AContentTypeOutsideTheClosedList_IsRefused(string contentType) =>
        Assert.Throws<ArgumentException>(() => new EmailAttachment("a.pdf", contentType, Pdf));

    [Fact]
    public void AnEmptyAttachment_IsRefused() =>
        Assert.Throws<ArgumentException>(() => new EmailAttachment("a.pdf", "application/pdf", []));

    [Fact]
    public void AnAttachmentOfExactlyTheMaximumSize_IsAccepted() =>
        Assert.Equal(EmailAttachment.MaxBytes, new EmailAttachment("a.pdf", "application/pdf", new byte[EmailAttachment.MaxBytes]).Content.Length);

    [Fact]
    public void AnAttachmentOneByteOverTheMaximum_IsRefused() =>
        Assert.Throws<ArgumentException>(() => new EmailAttachment("a.pdf", "application/pdf", new byte[EmailAttachment.MaxBytes + 1]));

    [Fact]
    public void TheMaximumSize_IsFiveMegabytes() => Assert.Equal(5 * 1024 * 1024, EmailAttachment.MaxBytes);

    [Fact]
    public void NullParts_AreAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => new EmailAttachment(null!, "application/pdf", Pdf));
        Assert.Throws<ArgumentNullException>(() => new EmailAttachment("a.pdf", null!, Pdf));
        Assert.Throws<ArgumentNullException>(() => new EmailAttachment("a.pdf", "application/pdf", null!));
    }

    [Fact]
    public void ToString_PrintsNeitherTheNameNorTheContent()
    {
        var attachment = new EmailAttachment("presupuesto-P-0012.pdf", "application/pdf", Pdf);

        var text = attachment.ToString();

        Assert.DoesNotContain("0012", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PDF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmailMessage_PrintsNothingOfItsContent()
    {
        var message = new EmailMessage(Email.Create("cliente-marcado@ejemplo.com"), "Asunto marcado", "Cuerpo marcado", Email.Create("empresa-marcada@ejemplo.com"),
            new EmailAttachment("a.pdf", "application/pdf", Pdf));

        var text = message.ToString();

        Assert.DoesNotContain("marcad", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ejemplo.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a.pdf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageBuiltTheOldWay_HasNeitherReplyToNorAttachment()
    {
        var message = new EmailMessage(Email.Create("ana@example.com"), "Asunto", "Cuerpo");

        Assert.Null(message.ReplyTo);
        Assert.Null(message.Attachment);
    }
}

using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Mail;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// El adjunto y la dirección de respuesta del correo (spec 026, R13, R14, D12), contra un servidor SMTP de verdad: se lee de vuelta lo que el servidor <b>recibió</b>. Lo que más importa
/// es que <b>sin</b> adjunto ni respuesta el mensaje sale como siempre (las invitaciones y la recuperación de contraseña no cambian) y que ninguna cabecera se puede inyectar.
/// </summary>
public class SmtpEmailSenderAttachmentTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private const string From = "no-reply@ofiflow.example";

    private static Email NewRecipient() => Email.Create($"destino-{Guid.NewGuid():N}@ejemplo.com");

    // Un PDF de mentira, pero con TODOS los valores de byte: si algo en el camino tratara el adjunto como texto, lo estropearía.
    private static byte[] BinaryPdf()
    {
        var bytes = new List<byte>("%PDF-1.7\n"u8.ToArray());
        for (var i = 0; i < 256; i++)
        {
            bytes.Add((byte)i);
        }

        bytes.AddRange("\n%%EOF"u8.ToArray());
        return [.. bytes];
    }

    private EmailOptions Options() => new()
    {
        Provider = EmailProvider.Smtp,
        From = From,
        FromName = "OfiFlow (pruebas)",
        LinkBaseUrl = "https://app.ofiflow.example",
        Smtp = new SmtpOptions
        {
            Host = mailpit.StartTls.Host,
            Port = mailpit.StartTls.SmtpPort,
            Security = SmtpSecurity.StartTls,
            UserName = MailpitFixture.UserName,
            Password = MailpitFixture.Password,
            Timeout = TimeSpan.FromSeconds(10)
        }
    };

    private SmtpEmailSender Sender(EmailOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), new ListLogger<SmtpEmailSender>(),
            (_, certificate, _, _) => certificate is not null && new X509Certificate2(certificate).Thumbprint == mailpit.CertificateThumbprint);

    private static Email EmailWithRawValue(string value)
    {
        var email = (Email)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Email));
        typeof(Email).GetField("<Value>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(email, value);
        return email;
    }

    // ===== El adjunto =====

    [Fact]
    public async Task AMessageWithAPdf_ArrivesWithTheTextAndExactlyOneAttachment_ByteForByte()
    {
        var to = NewRecipient();
        var pdf = BinaryPdf();
        var message = new EmailMessage(to, "Presupuesto P-0012", "Hola, va el presupuesto.\r\nSaludos: ñandú.", Attachment: new EmailAttachment("presupuesto-P-0012.pdf", "application/pdf", pdf));

        await Sender(Options()).SendAsync(message, CancellationToken.None);

        var received = await mailpit.StartTls.FindAsync(to.Value);
        Assert.NotNull(received);
        Assert.Equal("Presupuesto P-0012", received.Subject);
        Assert.Contains("Hola, va el presupuesto.", received.Text);
        Assert.Contains("ñandú", received.Text);
        Assert.Equal([to.Value], received.ToAddresses);
        Assert.Equal(0, received.CcCount);
        Assert.Equal(0, received.BccCount);

        var attachment = Assert.Single(received.Attachments);
        Assert.Equal("presupuesto-P-0012.pdf", attachment.FileName);
        Assert.StartsWith("application/pdf", attachment.ContentType, StringComparison.Ordinal);
        Assert.Equal(pdf, await received.ContentOfAsync(attachment));
    }

    [Fact]
    public async Task TheAttachment_TravelsAsAnAttachmentInAMixedMessage_EncodedInBase64()
    {
        var to = NewRecipient();
        await Sender(Options()).SendAsync(
            new EmailMessage(to, "Asunto", "Cuerpo", Attachment: new EmailAttachment("presupuesto-P-0012.pdf", "application/pdf", BinaryPdf())), CancellationToken.None);

        var raw = (await mailpit.StartTls.FindAsync(to.Value))!.Raw;

        Assert.Contains("multipart/mixed", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Transfer-Encoding: base64", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Disposition: attachment", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("presupuesto-P-0012.pdf", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAsciiOnlyAttachment_IsStillBase64_NotSevenBit()
    {
        // Con bytes binarios la biblioteca corrige una codificación equivocada sola; con un contenido que cabe en ASCII no: ahí se ve la que se pidió.
        var to = NewRecipient();
        await Sender(Options()).SendAsync(new EmailMessage(to, "Asunto", "Cuerpo", Attachment: new EmailAttachment("a.pdf", "application/pdf", "%PDF-1.7 solo ascii"u8.ToArray())), CancellationToken.None);

        var raw = (await mailpit.StartTls.FindAsync(to.Value))!.Raw;

        Assert.Contains("Content-Transfer-Encoding: base64", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMessageWithoutAttachmentOrReplyTo_IsExactlyTheOldOne()
    {
        // Las invitaciones y la recuperación de contraseña no llevan ni adjunto ni respuesta: su mensaje no puede haber cambiado.
        var to = NewRecipient();

        await Sender(Options()).SendAsync(new EmailMessage(to, "Invitación", "Cuerpo de siempre"), CancellationToken.None);

        var received = (await mailpit.StartTls.FindAsync(to.Value))!;
        Assert.Empty(received.Attachments);
        Assert.Empty(received.ReplyToAddresses);
        Assert.Contains("Content-Type: text/plain", received.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("multipart", received.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reply-To", received.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cuerpo de siempre", received.Text);
    }

    [Fact]
    public async Task TheBody_CannotAddAPartOrAHeader_WhenTheMessageHasAnAttachment()
    {
        // Quien controle el cuerpo no puede cerrar la parte de texto y abrir otra ni añadir destinatarios: viaja codificado dentro de su parte.
        var to = NewRecipient();
        var body = "Hola\r\n\r\n--frontera\r\nContent-Type: text/html\r\n\r\n<script>x</script>\r\nBcc: otro@ejemplo.com";

        await Sender(Options()).SendAsync(new EmailMessage(to, "Asunto", body, Attachment: new EmailAttachment("a.pdf", "application/pdf", BinaryPdf())), CancellationToken.None);

        var received = (await mailpit.StartTls.FindAsync(to.Value))!;
        Assert.Single(received.Attachments);
        Assert.Equal(0, received.BccCount);
        Assert.Equal([to.Value], received.ToAddresses);
    }

    // ===== Reply-To =====

    [Fact]
    public async Task TheReplyTo_IsTheCompanyAddress_AndTheSenderStaysTheConfiguredOne()
    {
        var to = NewRecipient();
        var company = Email.Create($"empresa-{Guid.NewGuid():N}@fontaneria.example");

        await Sender(Options()).SendAsync(new EmailMessage(to, "Asunto", "Cuerpo", company), CancellationToken.None);

        var received = (await mailpit.StartTls.FindAsync(to.Value))!;
        Assert.Equal([company.Value], received.ReplyToAddresses);
        Assert.Equal(From, received.FromAddress);
        Assert.Equal("OfiFlow (pruebas)", received.FromName);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" ")]
    [InlineData(">\r\nBcc: otro@ejemplo.com")]
    [InlineData(",otro@ejemplo.com")]
    public async Task AReplyToThatSlippedPastTheDomain_IsRejectedBeforeTouchingTheNetwork(string suffix)
    {
        var to = NewRecipient();
        var replyTo = EmailWithRawValue($"empresa@fontaneria.example{suffix}");
        var options = Options();
        options.Smtp.Host = "no-debe-conectar.invalid";

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(options).SendAsync(new EmailMessage(to, "Asunto", "Cuerpo", replyTo), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Rejected, exception.Category);
    }
}

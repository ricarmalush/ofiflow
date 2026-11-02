using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Mail;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// El transporte SMTP contra un servidor de verdad (spec 017, bloque 1, D11): que entrega el mensaje entero, que cifra, que autentica y que, cuando algo
/// falla, dice solo la categoría. Los fallos que un servidor real no sabe provocar (silencio, cortes, rechazos a medida) están en <see cref="SmtpEmailSenderFailureTests"/>.
/// </summary>
public class SmtpEmailSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private const string From = "no-reply@ofiflow.example";

    private static Email NewRecipient() => Email.Create($"destino-{Guid.NewGuid():N}@ejemplo.com");

    private EmailOptions Options(MailpitFixture.Endpoint endpoint, SmtpSecurity security, bool withCredentials) => new()
    {
        Provider = EmailProvider.Smtp,
        From = From,
        FromName = "OfiFlow (pruebas)",
        LinkBaseUrl = "https://app.ofiflow.example",
        Smtp = new SmtpOptions
        {
            Host = endpoint.Host,
            Port = endpoint.SmtpPort,
            Security = security,
            UserName = withCredentials ? MailpitFixture.UserName : null,
            Password = withCredentials ? MailpitFixture.Password : null,
            Timeout = TimeSpan.FromSeconds(10)
        }
    };

    // El certificado del servidor de pruebas es autofirmado: se acepta SOLO el que generó el fixture, comparando su huella.
    private SmtpEmailSender Sender(EmailOptions options, ListLogger<SmtpEmailSender>? logger = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options), logger ?? new ListLogger<SmtpEmailSender>(),
            (_, certificate, _, _) => certificate is not null && new X509Certificate2(certificate).Thumbprint == mailpit.CertificateThumbprint);

    // ===== Entrega =====

    [Fact]
    public async Task WithStartTlsAndCredentials_DeliversTheWholeMessage()
    {
        var to = NewRecipient();
        var sender = Sender(Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true));

        await sender.SendAsync(new EmailMessage(to, "Invitación a OfiFlow", "Hola, te han invitado.\nSaludos desde España: ñandú, acción, 日本語."), CancellationToken.None);

        var received = await mailpit.StartTls.FindAsync(to.Value);
        Assert.NotNull(received);
        Assert.Equal("Invitación a OfiFlow", received.Subject);
        Assert.Contains("ñandú, acción, 日本語", received.Text);
        Assert.Equal(From, received.FromAddress);
        Assert.Equal("OfiFlow (pruebas)", received.FromName);
        Assert.Equal([to.Value], received.ToAddresses);
        Assert.Equal(0, received.CcCount);
        Assert.Equal(0, received.BccCount);
    }

    [Fact]
    public async Task WithTlsFromTheFirstByte_DeliversTheMessage()
    {
        // Con un servidor en proceso y no con Mailpit: ver el comentario de MailpitFixture. Aquí NO se fija la huella del certificado, a diferencia de los demás tests: en
        // la máquina de desarrollo el antivirus (AVG) vuelve a firmar incluso esta conexión de bucle local y el cliente ve otro certificado. Lo que prueba este test es que el
        // transporte habla TLS desde el primer byte y entrega el mensaje; que el certificado se valida de verdad lo prueban los de STARTTLS y «ACertificateThatIsNotTheExpectedOne_IsRefused».
        using var server = ScriptedSmtpServer.AcceptingOverTls(mailpit.ServerCertificate);
        var to = NewRecipient();
        var options = Options(new MailpitFixture.Endpoint("127.0.0.1", server.Port, 0), SmtpSecurity.SslOnConnect, withCredentials: false);

        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(options), new ListLogger<SmtpEmailSender>(), (_, certificate, _, _) => certificate is not null);

        await sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo del mensaje"), CancellationToken.None);

        Assert.Contains(server.ClientLines, line => line == $"RCPT TO:<{to.Value}>");
        Assert.Contains(server.ClientLines, line => line == "Cuerpo del mensaje");
        Assert.Contains(server.ClientLines, line => line == "Subject: Asunto");
    }

    [Fact]
    public async Task WithoutEncryptionAndWithoutCredentials_DeliversToAServerOnThisMachine()
    {
        // La única excepción a «el correo va cifrado» (R5): un servidor en esta misma máquina, para desarrollo.
        var to = NewRecipient();
        var sender = Sender(Options(mailpit.Plain, SmtpSecurity.None, withCredentials: false));

        await sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None);

        Assert.NotNull(await mailpit.Plain.FindAsync(to.Value));
    }

    [Fact]
    public async Task TheMessageId_UsesTheSenderDomain_NotTheNameOfTheMachine()
    {
        var to = NewRecipient();
        var sender = Sender(Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true));

        await sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None);

        var received = await mailpit.StartTls.FindAsync(to.Value);
        Assert.NotNull(received);
        Assert.EndsWith("@ofiflow.example", received.MessageId);
        Assert.DoesNotContain(Environment.MachineName, received.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABodyThatLooksLikeHeaders_StaysInsideTheBody()
    {
        // Quien controle el texto del cuerpo no puede añadir destinatarios: el cuerpo viaja detrás de una línea en blanco.
        var to = NewRecipient();
        var sender = Sender(Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true));

        await sender.SendAsync(new EmailMessage(to, "Asunto", "Hola\r\n\r\nBcc: otro@ejemplo.com\r\nTo: tercero@ejemplo.com\r\n.\r\nfin"), CancellationToken.None);

        var received = await mailpit.StartTls.FindAsync(to.Value);
        Assert.NotNull(received);
        Assert.Equal([to.Value], received.ToAddresses);
        Assert.Equal(0, received.BccCount);
        Assert.Contains("Bcc: otro@ejemplo.com", received.Text);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync("otro@ejemplo.com"));
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync("tercero@ejemplo.com"));
    }

    // ===== Lo que no se entrega =====

    [Fact]
    public async Task WithTheWrongPassword_FailsAsAnAuthenticationProblem_AndDeliversNothing()
    {
        var to = NewRecipient();
        var options = Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true);
        options.Smtp.Password = "otra-contraseña";
        var logger = new ListLogger<SmtpEmailSender>();

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(options, logger).SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Authentication, exception.Category);
        AssertNothingSensitive(exception, logger, to, "otra-contraseña", MailpitFixture.Password);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync(to.Value));
    }

    [Fact]
    public async Task WhenTheServerAsksForCredentialsAndNoneAreConfigured_FailsAsAnAuthenticationProblem()
    {
        // Un fallo de configuración habitual: el servidor exige usuario y no se puso. Es de las credenciales, no del destinatario ni de la red.
        var to = NewRecipient();
        var sender = Sender(Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: false));

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Authentication, exception.Category);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync(to.Value));
    }

    [Fact]
    public async Task WithACertificateItDoesNotTrust_FailsAndDeliversNothing()
    {
        // El constructor público es el de producción: validación normal del sistema, sin aceptar nada a medida. El certificado autofirmado del
        // servidor de pruebas no es de confianza, así que el envío no debe llegar a mandar el mensaje.
        var to = NewRecipient();
        var options = Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true);
        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(options), new ListLogger<SmtpEmailSender>());

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Unavailable, exception.Category);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync(to.Value));
    }

    [Fact]
    public async Task ACertificateThatIsNotTheExpectedOne_IsRefused()
    {
        // Control del test de arriba: la comprobación por huella no acepta cualquier certificado.
        var to = NewRecipient();
        var options = Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true);
        var sender = new SmtpEmailSender(Microsoft.Extensions.Options.Options.Create(options), new ListLogger<SmtpEmailSender>(),
            (_, certificate, _, _) => certificate is not null && new X509Certificate2(certificate).Thumbprint == "0000000000000000000000000000000000000000");

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Unavailable, exception.Category);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync(to.Value));
    }

    [Fact]
    public async Task SendingCredentialsWithoutEncryption_DoesNotDeliver()
    {
        // Este servidor solo acepta autenticar ya cifrado. Aunque la configuración no lo permitiría (R5 solo admite «sin cifrar» en esta máquina, y esto lo es),
        // el servidor rechaza las credenciales en claro: nada se entrega.
        var to = NewRecipient();
        var sender = Sender(Options(mailpit.StartTls, SmtpSecurity.None, withCredentials: true));

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Authentication, exception.Category);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync(to.Value));
    }

    // ===== Cabeceras: lo que no puede inyectarse =====

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData(" ")]
    [InlineData(">\r\nBcc: otro@ejemplo.com")]
    public async Task ARecipientThatSlippedPastTheDomainWithControlCharacters_IsRejectedBeforeTouchingTheNetwork(string suffix)
    {
        // Defensa en profundidad (R7): el transporte es el último sitio antes de la red y no confía en que el dominio ya lo haya comprobado. Hubo un tiempo en que
        // Email.Create dejaba pasar un salto de línea final (su expresión usaba «$»); hoy ya no, así que el valor se fabrica SALTÁNDOSE el dominio, como lo haría un fallo futuro.
        var address = $"destino-{Guid.NewGuid():N}@ejemplo.com";
        var to = EmailWithRawValue(address + suffix);
        var options = Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true);
        options.Smtp.Host = "no-debe-conectar.invalid";

        var exception = await Assert.ThrowsAsync<EmailDeliveryException>(() => Sender(options).SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), CancellationToken.None));

        Assert.Equal(EmailFailureCategory.Rejected, exception.Category);
        Assert.False(await mailpit.StartTls.ReceivedAnythingForAsync(address));
    }

    private static Email EmailWithRawValue(string value)
    {
        var email = (Email)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Email));
        typeof(Email).GetField("<Value>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(email, value);
        return email;
    }

    [Theory]
    [InlineData("Asunto\r\nBcc: otro@ejemplo.com")]
    [InlineData("Asunto\nBcc: otro@ejemplo.com")]
    [InlineData("Asunto\0")]
    public async Task ASubjectWithControlCharacters_IsRefused_BeforeTouchingTheNetwork(string subject)
    {
        var options = Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true);
        options.Smtp.Host = "no-debe-conectar.invalid";

        await Assert.ThrowsAsync<ArgumentException>(() => Sender(options).SendAsync(new EmailMessage(NewRecipient(), subject, "Cuerpo"), CancellationToken.None));
    }

    // ===== Cancelación =====

    [Fact]
    public async Task WhenTheCallerCancels_ItIsACancellation_NotADeliveryFailure()
    {
        var to = NewRecipient();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sender(Options(mailpit.StartTls, SmtpSecurity.StartTls, withCredentials: true)).SendAsync(new EmailMessage(to, "Asunto", "Cuerpo"), cancelled.Token));
    }

    internal static void AssertNothingSensitive(EmailDeliveryException exception, ListLogger<SmtpEmailSender> logger, Email to, params string[] secrets)
    {
        var everything = exception.Message + " " + exception.ToString() + " " + string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain(to.Value, everything);
        Assert.DoesNotContain(to.Value.Split('@')[0], everything);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, everything);
        }
    }
}

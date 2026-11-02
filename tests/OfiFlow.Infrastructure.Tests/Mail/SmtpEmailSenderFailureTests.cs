using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Mail;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// Cómo falla el transporte SMTP (spec 017, R1 y R6): cada fallo se traduce a una categoría de una lista cerrada y no se escapa ni la dirección ni el texto del
/// servidor. Se prueba con un servidor SMTP de mentira dentro del test, que provoca justo el fallo que se quiere; no hace falta Docker.
/// </summary>
public class SmtpEmailSenderFailureTests
{
    private static readonly Email Recipient = Email.Create("persona.secreta@ejemplo.com");

    private static SmtpEmailSender Sender(int port, SmtpSecurity security, TimeSpan? timeout = null, ListLogger<SmtpEmailSender>? logger = null) =>
        new(Options.Create(new EmailOptions
        {
            Provider = EmailProvider.Smtp,
            From = "no-reply@ofiflow.example",
            LinkBaseUrl = "https://app.ofiflow.example",
            Smtp = new SmtpOptions { Host = "127.0.0.1", Port = port, Security = security, Timeout = timeout ?? TimeSpan.FromSeconds(5) }
        }), logger ?? new ListLogger<SmtpEmailSender>());

    private static Task<EmailDeliveryException> SendExpectingFailure(SmtpEmailSender sender) =>
        Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage(Recipient, "Asunto", "Cuerpo"), CancellationToken.None));

    [Fact]
    public async Task AServerThatNeverAnswers_TimesOutAtTheConfiguredTime_AndDoesNotRetry()
    {
        using var server = ScriptedSmtpServer.Silent();
        var stopwatch = Stopwatch.StartNew();

        var exception = await SendExpectingFailure(Sender(server.Port, SmtpSecurity.None, timeout: TimeSpan.FromSeconds(1)));

        stopwatch.Stop();
        Assert.Equal(EmailFailureCategory.Timeout, exception.Category);
        // Un segundo de presupuesto, no más (R6): ni un reintento ni una espera por defecto de la biblioteca. Holgura para una máquina cargada.
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task ARecipientThatTheServerRejects_IsRejected_AndTheServerTextNeverEscapes()
    {
        using var server = ScriptedSmtpServer.RejectsTheRecipient();
        var logger = new ListLogger<SmtpEmailSender>();

        var exception = await SendExpectingFailure(Sender(server.Port, SmtpSecurity.None, logger: logger));

        Assert.Equal(EmailFailureCategory.Rejected, exception.Category);
        // Control: el servidor SÍ recibió y repitió la dirección en su respuesta; lo que se comprueba es que no salió.
        Assert.Contains(server.ClientLines, line => line.Contains("persona.secreta@ejemplo.com", StringComparison.Ordinal));
        SmtpEmailSenderTests.AssertNothingSensitive(exception, logger, Recipient, "User unknown", "Recipient address rejected");
        Assert.DoesNotContain(server.ClientLines, line => line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase)); // no llegó a mandar el mensaje
    }

    [Fact]
    public async Task WhenEncryptionIsRequiredButTheServerDoesNotOfferIt_ItFailsInsteadOfSendingInTheClear()
    {
        using var server = ScriptedSmtpServer.WithoutStartTls();

        var exception = await SendExpectingFailure(Sender(server.Port, SmtpSecurity.StartTls));

        Assert.Equal(EmailFailureCategory.Unavailable, exception.Category);
        Assert.DoesNotContain(server.ClientLines, line => line.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.ClientLines, line => line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AServerThatClosesTheConnection_IsUnavailable()
    {
        using var server = ScriptedSmtpServer.DropsTheConnection();

        var exception = await SendExpectingFailure(Sender(server.Port, SmtpSecurity.None));

        Assert.Equal(EmailFailureCategory.Unavailable, exception.Category);
    }

    [Fact]
    public async Task AServerThatRefusesTheGreeting_IsUnavailable()
    {
        using var server = ScriptedSmtpServer.RefusesTheGreeting();

        var exception = await SendExpectingFailure(Sender(server.Port, SmtpSecurity.None));

        Assert.Equal(EmailFailureCategory.Unavailable, exception.Category);
    }

    [Fact]
    public async Task NothingListening_IsUnavailable_AndFast()
    {
        // Se reserva un puerto libre y se suelta: nadie escucha en él.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var stopwatch = Stopwatch.StartNew();

        var exception = await SendExpectingFailure(Sender(closedPort, SmtpSecurity.None));

        Assert.Equal(EmailFailureCategory.Unavailable, exception.Category);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), "Una conexión rechazada debe fallar enseguida, no esperar al tiempo máximo.");
    }

    [Fact]
    public async Task TlsAgainstAServerThatDoesNotSpeakTls_IsUnavailable()
    {
        using var server = ScriptedSmtpServer.WithoutStartTls();

        var exception = await SendExpectingFailure(Sender(server.Port, SmtpSecurity.SslOnConnect, timeout: TimeSpan.FromSeconds(3)));

        Assert.True(exception.Category is EmailFailureCategory.Unavailable or EmailFailureCategory.Timeout, $"Categoría inesperada: {exception.Category}");
        Assert.DoesNotContain(server.ClientLines, line => line.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AFailure_LeavesOneWarningWithTheCategoryAndTheExceptionType_AndNothingElse()
    {
        using var server = ScriptedSmtpServer.RejectsTheRecipient();
        var logger = new ListLogger<SmtpEmailSender>();

        await SendExpectingFailure(Sender(server.Port, SmtpSecurity.None, logger: logger));

        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("Rejected", warning.Message);
        Assert.Contains("Exception", warning.Message);
        Assert.DoesNotContain("persona.secreta", warning.Message);
    }
}

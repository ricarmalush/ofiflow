using System.Net.Security;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// El transporte SMTP del puerto de correo (spec 017, D1, ADR-020), con MailKit. Una conexión por mensaje: el volumen es el de unas invitaciones por hora, y
/// mantener conexiones abiertas añadiría estado (y fallos por conexiones caducadas) a cambio de nada.
/// </summary>
/// <remarks>
/// Lo que hace a propósito:
/// <list type="bullet">
/// <item><b>Cifrado obligatorio</b> salvo servidor de bucle local (R5), con la validación normal del certificado: este fichero no la desactiva en ningún caso.</item>
/// <item><b>Un solo tiempo máximo</b> para todo el envío (conectar, autenticar, enviar) y <b>sin reintentos</b> (R6).</item>
/// <item><b>Cabeceras a salvo</b> (R7): el asunto y el destinatario con caracteres de control se rechazan antes de tocar la red; el remitente viene de configuración validada.</item>
/// <item><b>Errores sin datos</b> (R1): solo sale una <see cref="EmailFailureCategory"/>. El texto del servidor, que suele repetir la dirección, se descarta; en el
/// registro queda el <i>tipo</i> de la excepción, que no lleva datos.</item>
/// </list>
/// </remarks>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly RemoteCertificateValidationCallback? _certificateValidation;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
        : this(options, logger, certificateValidation: null)
    {
    }

    /// <summary>
    /// Solo para los tests: permite comprobar el certificado contra uno concreto (el que el test generó para su servidor). En producción es siempre <c>null</c> y rige
    /// la validación normal del sistema operativo.
    /// </summary>
    internal SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger, RemoteCertificateValidationCallback? certificateValidation)
    {
        _options = options.Value;
        _logger = logger;
        _certificateValidation = certificateValidation;
    }

    private enum Phase
    {
        Connect,
        Authenticate,
        Send
    }

    public bool IsEnabled => true;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = BuildMessage(message);
        var smtp = _options.Smtp;

        // Un único presupuesto de tiempo para todo el envío. Si se agota, se cancela la operación en curso, sea la que sea.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(smtp.Timeout);

        // El dominio con el que el cliente se presenta (EHLO): sin esto MailKit manda el nombre de la máquina, que acaba en la cabecera «Received» que ve el destinatario.
        using var client = new SmtpClient { LocalDomain = DomainOf(_options.From!) };
        client.Timeout = (int)smtp.Timeout.TotalMilliseconds;
        if (_certificateValidation is not null)
        {
            client.ServerCertificateValidationCallback = _certificateValidation;
            client.CheckCertificateRevocation = false;
        }

        var phase = Phase.Connect;
        try
        {
            await client.ConnectAsync(smtp.Host!, smtp.Port, ToSocketOptions(smtp.Security), timeout.Token);

            if (!string.IsNullOrEmpty(smtp.UserName))
            {
                phase = Phase.Authenticate;
                await client.AuthenticateAsync(smtp.UserName, smtp.Password!, timeout.Token);
            }

            phase = Phase.Send;
            await client.SendAsync(mime, timeout.Token);

            // El mensaje ya está aceptado: cerrar mal la conexión no lo deshace ni debe convertirse en un fallo.
            try
            {
                await client.DisconnectAsync(quit: true, timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("El mensaje se envió, pero cerrar la conexión SMTP falló ({ExceptionType}).", exception.GetType().Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Quien llamó canceló: no es un fallo del correo, se propaga tal cual.
            throw;
        }
        catch (Exception exception)
        {
            var category = Classify(exception, phase, timedOut: timeout.IsCancellationRequested);
            _logger.LogWarning("Falló un envío SMTP. Categoría {Category}, causa {ExceptionType}", category, exception.GetType().Name);
            throw new EmailDeliveryException(category);
        }
    }

    private MimeMessage BuildMessage(EmailMessage message)
    {
        // R7: el destinatario y el asunto no pueden llevar un salto de línea ni ningún carácter de control, porque inyectarían cabeceras. Se comprueba aquí
        // aunque quien llama ya valide: este es el último sitio antes de la red.
        var to = message.To.Value;
        if (!EmailOptions.IsPlainAddress(to))
        {
            throw new EmailDeliveryException(EmailFailureCategory.Rejected);
        }

        if (message.Subject.Any(char.IsControl))
        {
            throw new ArgumentException("El asunto de un correo no puede contener caracteres de control.", nameof(message));
        }

        var from = _options.From!;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, from));
        mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = message.Subject;
        // El identificador del mensaje por defecto usa el nombre de la máquina; con el dominio del remitente no se filtra ese dato.
        mime.MessageId = MimeUtils.GenerateMessageId(DomainOf(from));
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        return mime;
    }

    private static string DomainOf(string address) => address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        // StartTls (el obligatorio, no el oportunista): si el servidor no ofrece cifrado, falla en vez de enviar en claro.
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None
    };

    private static EmailFailureCategory Classify(Exception exception, Phase phase, bool timedOut)
    {
        if (timedOut || exception is TimeoutException)
        {
            return EmailFailureCategory.Timeout;
        }

        // Un fallo de TLS (certificado, protocolo) es «no se pudo llegar a un servidor de confianza», no un problema de credenciales.
        if (exception is SslHandshakeException)
        {
            return EmailFailureCategory.Unavailable;
        }

        return phase switch
        {
            // ServiceNotAuthenticatedException: el servidor exige usuario y no se configuró (o no se aceptó). Es de configuración, como una contraseña mala.
            _ when exception is ServiceNotAuthenticatedException => EmailFailureCategory.Authentication,
            Phase.Authenticate when exception is AuthenticationException or SmtpCommandException => EmailFailureCategory.Authentication,
            Phase.Send when exception is SmtpCommandException => EmailFailureCategory.Rejected,
            _ when exception is SmtpProtocolException or ProtocolException or IOException or System.Net.Sockets.SocketException or ServiceNotConnectedException
                or NotSupportedException or SmtpCommandException => EmailFailureCategory.Unavailable,
            _ => EmailFailureCategory.Unknown
        };
    }
}

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// La cola de correo en memoria (spec 018, D3, ADR-021): un canal <b>acotado</b> con un solo lector (<see cref="EmailDispatchService"/>). Un singleton por proceso:
/// con varias instancias de la API, cada una envía lo que recibió ella (nadie reparte ni duplica).
/// </summary>
/// <remarks>
/// Lo que garantiza: <b>no bloquea nunca</b> a quien encola (si está llena, descarta y lo registra: R15), <b>no persiste nada</b> (el mensaje, que puede llevar un
/// token, solo vive en la memoria del proceso hasta que se envía o se pierde con él) y <b>no registra el contenido</b> de ningún mensaje.
/// </remarks>
internal sealed partial class EmailQueue : IEmailQueue
{
    private readonly Channel<EmailMessage> _channel;
    private readonly bool _enabled;
    private readonly ILogger<EmailQueue> _logger;

    public EmailQueue(IOptions<EmailDispatchOptions> options, ILogger<EmailQueue> logger)
    {
        _enabled = options.Value.Enabled;
        _logger = logger;

        // Wait + TryWrite: con la cola llena, TryWrite devuelve false al instante en vez de esperar un hueco (un Wait de verdad bloquearía la petición).
        _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>De dónde lee el servicio que envía. Solo Infrastructure lo ve: Application solo encola.</summary>
    internal ChannelReader<EmailMessage> Reader => _channel.Reader;

    public bool TryEnqueue(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_enabled)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(message))
        {
            return true;
        }

        // R15: se descarta y se registra. Sin la dirección ni el contenido (puede llevar un enlace con un token).
        LogDiscarded(_logger);
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "La cola de correo está llena: se descartó un mensaje. Si se repite, el servidor de correo no está entregando o hay un abuso.")]
    private static partial void LogDiscarded(ILogger logger);
}

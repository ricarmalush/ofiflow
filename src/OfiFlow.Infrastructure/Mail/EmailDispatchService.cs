using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// El <b>segundo servicio en segundo plano</b> de OfiFlow, después de la purga (spec 018 R14, ADR-021): vacía la cola de correo (<see cref="EmailQueue"/>) y entrega
/// cada mensaje con el puerto <see cref="IEmailSender"/>, de uno en uno.
/// </summary>
/// <remarks>
/// Lo que cumple, como la purga (ADR-019): <b>no tumba la API</b> (un fallo se registra y sigue con el siguiente mensaje), se detiene limpiamente con la API, tiene
/// opciones validadas al arrancar y <b>no corre en la consola de administración</b>. Y lo que añade el correo: <b>sin reintentos</b> y <b>sin persistencia</b> (R15), y un fallo
/// deja un evento con la <b>categoría</b> y nunca la dirección, el texto del servidor ni el contenido del mensaje (R16).
/// </remarks>
internal sealed partial class EmailDispatchService(
    EmailQueue queue,
    IEmailSender sender,
    IOptions<EmailDispatchOptions> options,
    ILogger<EmailDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        try
        {
            // No hace falta un retraso inicial como el de la purga: sin mensajes, esperar en la cola no compite con el arranque ni con las migraciones.
            await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // La API se está deteniendo: salida limpia. Lo que quedara en la cola se pierde (R15).
        }
    }

    /// <summary>Entrega un mensaje. Público para el test, que lo llama sin esperar al lector.</summary>
    internal async Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (!sender.IsEnabled)
            {
                // Con el correo apagado ningún caso de uso debería encolar nada; si ocurriera, se descarta sin ruido.
                LogDiscardedWithoutTransport(logger);
                return;
            }

            await sender.SendAsync(message, cancellationToken);
        }
        catch (EmailDeliveryException exception)
        {
            // R16: solo la categoría. La excepción ya trae un mensaje fijo y ninguna dirección.
            LogFailed(logger, exception.Category.ToString(), nameof(EmailDeliveryException));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Un error inesperado de un mensaje no puede parar los siguientes. Solo el tipo: el texto de una excepción puede llevar datos.
            LogFailed(logger, "Unexpected", exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = SecurityEventIds.EmailDispatchFailed, Level = LogLevel.Warning,
        Message = "No se pudo enviar un correo de la cola. Categoría {Category}, causa {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string category, string exceptionType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Se descartó un correo de la cola: el correo no está configurado.")]
    private static partial void LogDiscardedWithoutTransport(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "El envío de la cola de correo está desactivado (EmailDispatch:Enabled = false).")]
    private static partial void LogDisabled(ILogger logger);
}

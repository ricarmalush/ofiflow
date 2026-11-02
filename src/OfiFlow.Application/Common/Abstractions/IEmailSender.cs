using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// El puerto de correo (spec 017, ADR-020): lo único que los casos de uso saben del envío. El transporte (hoy SMTP) vive en Infrastructure y se
/// cambia con configuración; añadir un uso nuevo (recuperar contraseña, avisos...) es construir un <see cref="EmailMessage"/>, no repensar el envío.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Entrega el mensaje al servidor de correo y espera su aceptación (envío directo y sin cola: D2). No reintenta (R6).
    /// </summary>
    /// <exception cref="EmailDeliveryException">El envío falló. Solo trae la categoría, nunca la dirección ni el texto del servidor.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

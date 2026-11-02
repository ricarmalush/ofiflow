namespace OfiFlow.Application.Common.Mail;

/// <summary>
/// El envío de un correo falló (spec 017, R1). El mensaje es <b>fijo por categoría</b>: nunca contiene la dirección del destinatario ni el texto que
/// devolvió el servidor de correo, porque ese texto suele repetirla y los mensajes de excepción acaban en los logs (ADR-009 R5). La excepción
/// original no se conserva como <c>InnerException</c> por la misma razón.
/// </summary>
public sealed class EmailDeliveryException(EmailFailureCategory category) : Exception(MessageFor(category))
{
    public EmailFailureCategory Category { get; } = category;

    private static string MessageFor(EmailFailureCategory category) => category switch
    {
        EmailFailureCategory.Timeout => "El servidor de correo no contestó a tiempo.",
        EmailFailureCategory.Authentication => "El servidor de correo no aceptó las credenciales de OfiFlow.",
        EmailFailureCategory.Rejected => "El servidor de correo rechazó el mensaje.",
        EmailFailureCategory.Unavailable => "No se pudo conectar con el servidor de correo.",
        _ => "No se pudo enviar el correo."
    };
}

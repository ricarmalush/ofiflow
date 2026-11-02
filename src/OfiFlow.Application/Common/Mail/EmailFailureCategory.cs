namespace OfiFlow.Application.Common.Mail;

/// <summary>
/// Por qué falló un envío, en una lista cerrada (spec 017, R1). Es lo único que sale del transporte hacia los logs y los eventos: el texto de
/// error del servidor de correo puede llevar la dirección del destinatario, y los registros no pueden (ADR-009 R5).
/// </summary>
public enum EmailFailureCategory
{
    /// <summary>El servidor de correo no contestó a tiempo (o no se pudo conectar a tiempo).</summary>
    Timeout,

    /// <summary>El servidor de correo no aceptó el usuario y la contraseña de OfiFlow: es un fallo de <b>configuración</b>, no del destinatario.</summary>
    Authentication,

    /// <summary>El servidor de correo rechazó este mensaje o este destinatario (dirección que no existe, política del servidor...).</summary>
    Rejected,

    /// <summary>No hay servidor de correo al que llegar: caído, sin red, o conexión cortada.</summary>
    Unavailable,

    /// <summary>Cualquier otra cosa. Se trata como un fallo y se investiga.</summary>
    Unknown
}

using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// La cola de correo (spec 018, R2, R14, R15, D3): los casos de uso que no pueden esperar al servidor de correo dejan aquí el mensaje y siguen. Otra tarea lo envía.
/// </summary>
/// <remarks>
/// Es <b>en memoria</b> a propósito. El correo de restablecimiento lleva un token: una cola en la base de datos lo guardaría en claro, que es justo lo que se evita
/// guardando solo el hash. Y enviarlo <i>dentro</i> de la petición tardaría segundos solo cuando la cuenta existe, un <b>oráculo de tiempo</b>. Lo que se acepta a cambio: lo
/// pendiente se pierde si la API se reinicia, no hay reintentos y la persona vuelve a pedirlo (ADR-021).
/// </remarks>
public interface IEmailQueue
{
    /// <summary>
    /// Deja el mensaje en la cola y vuelve <b>sin esperar al envío</b>. No lanza si no puede: devuelve <c>false</c> (la cola está llena o el envío está desactivado) y el mensaje
    /// se descarta. Quien llama <b>no debe</b> cambiar su respuesta según el resultado: hacerlo delataría el estado interno de la cola.
    /// </summary>
    bool TryEnqueue(EmailMessage message);
}

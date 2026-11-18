using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Common.Mail;

/// <summary>
/// Un correo que OfiFlow envía (spec 017, R1 y R2): <b>un</b> destinatario, un asunto y un cuerpo de <b>texto plano</b>. Sin copias, sin copias
/// ocultas y sin HTML: ningún uso previsto los necesita y cada uno es una vía más de fuga o de inyección. Desde la spec 026 admite además, <b>y solo si el caso de uso lo pide</b>, una
/// dirección de respuesta y <b>un</b> adjunto controlado (<see cref="EmailAttachment"/>); sin ellos, el mensaje es exactamente el de antes.
/// </summary>
/// <param name="To">El destinatario, ya validado como email (el mismo tipo que usa el resto de la aplicación).</param>
/// <param name="Subject">El asunto. Lo escribe el código de OfiFlow, <b>nunca</b> un dato de fuera (R7): por eso no puede inyectar cabeceras.</param>
/// <param name="TextBody">El cuerpo, en texto plano.</param>
/// <param name="ReplyTo">A quién responde quien reciba el correo (spec 026, R13): la empresa que manda el presupuesto, no OfiFlow. <c>null</c>: sin dirección de respuesta.</param>
/// <param name="Attachment">El adjunto, si lo hay (spec 026, R14): un PDF ya validado. <c>null</c>: sin adjunto.</param>
public sealed record EmailMessage(Email To, string Subject, string TextBody, Email? ReplyTo = null, EmailAttachment? Attachment = null)
{
    // El ToString de un record imprime todas sus propiedades: aquí hay la dirección de un tercero, el cuerpo y un documento (ADR-009 R5).
    public override string ToString() => nameof(EmailMessage);
}

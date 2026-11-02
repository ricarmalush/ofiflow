using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Common.Mail;

/// <summary>
/// Un correo que OfiFlow envía (spec 017, R1 y R2): <b>un</b> destinatario, un asunto y un cuerpo de <b>texto plano</b>. Sin copias, sin copias
/// ocultas, sin HTML y sin adjuntos: ningún uso previsto los necesita y cada uno es una vía más de fuga o de inyección.
/// </summary>
/// <param name="To">El destinatario, ya validado como email (el mismo tipo que usa el resto de la aplicación).</param>
/// <param name="Subject">El asunto. Lo escribe el código de OfiFlow, <b>nunca</b> un dato de fuera (R7): por eso no puede inyectar cabeceras.</param>
/// <param name="TextBody">El cuerpo, en texto plano.</param>
public sealed record EmailMessage(Email To, string Subject, string TextBody);

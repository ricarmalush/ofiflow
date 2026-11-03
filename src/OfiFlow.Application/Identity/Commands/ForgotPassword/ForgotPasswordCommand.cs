using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.ForgotPassword;

/// <summary>
/// «He olvidado mi contraseña» (spec 018). Es anónima (la persona no puede iniciar sesión: por eso lo pide) y <b>responde siempre lo mismo</b>: exista o no la cuenta, esté bloqueada,
/// sea de una empresa suspendida o de un operador, esté el correo apagado o se haya pasado el límite de la cuenta (R1). Si hay cuenta, deja el correo con el enlace en la cola; no
/// devuelve nada, y menos el token: quien pide el restablecimiento de una cuenta no puede ser quien recibe el permiso para cambiarla.
/// </summary>
[AllowAnonymousRequest]
public sealed record ForgotPasswordCommand(string Email) : IRequest;

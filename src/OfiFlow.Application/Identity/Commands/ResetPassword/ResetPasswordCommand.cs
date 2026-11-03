using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.ResetPassword;

/// <summary>
/// Elegir una contraseña nueva con el token que llegó por correo (spec 018, R7-R13). Anónimo: quien la olvidó no puede tener sesión; la credencial es el token.
/// </summary>
/// <param name="Token">El token en claro del enlace del correo. Un secreto.</param>
/// <param name="NewPassword">La contraseña nueva, en claro hasta hashearla. Un secreto.</param>
[AllowAnonymousRequest]
public sealed record ResetPasswordCommand(string Token, string NewPassword) : IRequest
{
    // El ToString de un record escribe todas sus propiedades, y aquí dos son secretos. Nada lo llama hoy (ningún comportamiento registra las peticiones), pero un día alguien
    // escribirá «logger.LogDebug("{Request}", request)» y la contraseña acabaría en el fichero: así no puede.
    public override string ToString() => nameof(ResetPasswordCommand);
}

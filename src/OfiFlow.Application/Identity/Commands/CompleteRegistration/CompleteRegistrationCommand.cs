using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.CompleteRegistration;

/// <summary>
/// Completar el registro con el token que llegó por correo y elegir la contraseña (spec 021, R10-R15). Anónimo: quien se registra todavía no tiene cuenta, y la credencial es el token.
/// Es aquí, y no al pedir el registro, donde <b>nace</b> la cuenta: la empresa, el usuario y su pertenencia como <c>Owner</c>.
/// </summary>
/// <param name="Token">El token en claro del enlace del correo. Un secreto.</param>
/// <param name="NewPassword">La contraseña que elige ahora, en claro hasta hashearla. Un secreto.</param>
[AllowAnonymousRequest]
public sealed record CompleteRegistrationCommand(string Token, string NewPassword) : IRequest
{
    // El ToString de un record escribe todas sus propiedades, y aquí dos son secretos. Nada lo llama hoy, pero un día alguien escribirá «logger.LogDebug("{Request}", request)» y la
    // contraseña acabaría en el fichero: así no puede.
    public override string ToString() => nameof(CompleteRegistrationCommand);
}

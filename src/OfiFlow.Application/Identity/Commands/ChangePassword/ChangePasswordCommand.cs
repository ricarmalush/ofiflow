using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.ChangePassword;

/// <summary>
/// Cambiar la <b>propia</b> contraseña estando dentro (spec 019, ADR-025). Exige la contraseña actual y, si la cuenta tiene el segundo factor activo, un código (de la app o de recuperación): una sesión robada
/// no basta. La persona sale del token, nunca de la petición. Cambia la contraseña, cierra las <b>demás</b> sesiones (esta se conserva), anula los enlaces de recuperación pendientes y avisa por correo, todo
/// o nada. Deliberadamente <b>no</b> lleva <c>[AllowedWithoutSecondFactor]</c>: un <c>Owner</c> o <c>Admin</c> sin factor lo activa primero (spec 022).
/// </summary>
/// <param name="CurrentPassword">La contraseña actual, en claro. Un secreto.</param>
/// <param name="NewPassword">La contraseña nueva, en claro hasta hashearla. Un secreto.</param>
/// <param name="Code">El código del segundo factor (o uno de recuperación); se ignora si la cuenta no lo tiene activo. Un secreto de un solo uso.</param>
[RequiresPermission(Permission.AccountChangePassword)]
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword, string? Code = null) : IRequest
{
    // El ToString de un record escribe todas sus propiedades, y aquí las tres son secretos. Nada lo llama hoy, pero un día alguien escribirá «logger.LogDebug("{Request}", request)» y las
    // contraseñas acabarían en el fichero: así no puede (la misma lección de la spec 021).
    public override string ToString() => nameof(ChangePasswordCommand);
}

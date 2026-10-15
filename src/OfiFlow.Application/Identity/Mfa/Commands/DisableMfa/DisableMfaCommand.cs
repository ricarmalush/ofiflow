using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;

/// <summary>
/// Desactiva el segundo factor de la propia cuenta (spec 010 R7). Exige la contraseña <b>y</b> un código válido (el de
/// la app o uno de recuperación): quien solo tenga un token robado, o solo la contraseña, no puede quitarlo. Borra el
/// secreto y los códigos, y cierra todas las sesiones de la cuenta (R14).
/// </summary>
[RequiresPermission(Permission.MfaManage)]
public sealed record DisableMfaCommand(string Password, string Code) : IRequest;

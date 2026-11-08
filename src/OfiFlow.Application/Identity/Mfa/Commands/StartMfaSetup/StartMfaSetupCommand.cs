using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;

/// <summary>
/// Pide el secreto para activar el segundo factor (spec 010 R5). Exige la <b>contraseña actual</b>: un token de acceso
/// robado no basta para enrolar el móvil de otra persona. Deja el secreto como pendiente hasta confirmarlo con un código.
/// </summary>
[RequiresPermission(Permission.MfaManage)]
// Spec 022 R3: una de las CUATRO operaciones que puede hacer quien debe activar el segundo factor y aún no lo tiene (pedir el secreto: el primer paso de activarlo).
[AllowedWithoutSecondFactor]
public sealed record StartMfaSetupCommand(string Password) : IRequest<MfaSetupDto>;

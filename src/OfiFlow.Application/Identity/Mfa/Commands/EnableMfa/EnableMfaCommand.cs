using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;

/// <summary>
/// Confirma el secreto pendiente con el primer código de la app y activa el segundo factor (spec 010 R6). Devuelve los
/// códigos de recuperación, que se muestran <b>una sola vez</b>, y cierra todas las sesiones de la cuenta (R14).
/// </summary>
[RequiresPermission(Permission.MfaManage)]
// Spec 022 R3: una de las CUATRO operaciones que puede hacer quien debe activar el segundo factor y aún no lo tiene (confirmar el secreto con un primer código: el último paso de activarlo).
[AllowedWithoutSecondFactor]
public sealed record EnableMfaCommand(string Code) : IRequest<RecoveryCodesDto>;

using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;

/// <summary>
/// Genera códigos de recuperación nuevos (spec 010 R7): los anteriores, usados o no, dejan de valer. Exige la
/// contraseña y un código válido. Los nuevos se muestran <b>una sola vez</b>. No cierra las sesiones.
/// </summary>
[RequiresPermission(Permission.MfaManage)]
public sealed record RegenerateRecoveryCodesCommand(string Password, string Code) : IRequest<RecoveryCodesDto>;

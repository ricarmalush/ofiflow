using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Mfa.Queries.GetMfaStatus;

/// <summary>Cómo está el segundo factor de la <b>propia</b> cuenta (spec 010 R8). Nunca devuelve secretos ni códigos.</summary>
[RequiresPermission(Permission.MfaManage)]
// Spec 022 R3: una de las CUATRO operaciones que puede hacer quien debe activar el segundo factor y aún no lo tiene (ver el estado del factor: parte del asistente de activación).
[AllowedWithoutSecondFactor]
public sealed record GetMfaStatusQuery : IRequest<MfaStatusDto>;

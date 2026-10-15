using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Mfa.Queries.GetMfaStatus;

/// <summary>Cómo está el segundo factor de la <b>propia</b> cuenta (spec 010 R8). Nunca devuelve secretos ni códigos.</summary>
[RequiresPermission(Permission.MfaManage)]
public sealed record GetMfaStatusQuery : IRequest<MfaStatusDto>;

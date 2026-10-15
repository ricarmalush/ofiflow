using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Queries.GetInvitations;

/// <summary>Las invitaciones pendientes de la empresa activa que el usuario puede gestionar (sin el token).</summary>
[RequiresPermission(Permission.UsersManage)]
public sealed record GetInvitationsQuery : IRequest<List<InvitationDto>>;

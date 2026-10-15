using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.InviteUser;

/// <summary>Invita a una persona a la empresa activa con un rol (spec 009). Devuelve el token, una única vez.</summary>
[RequiresPermission(Permission.UsersManage)]
public sealed record InviteUserCommand(string Email, TenantRole Role) : IRequest<InvitationCreatedDto>;

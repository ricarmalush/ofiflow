using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.RevokeInvitation;

[RequiresPermission(Permission.UsersManage)]
public sealed record RevokeInvitationCommand(Guid Id) : IRequest;

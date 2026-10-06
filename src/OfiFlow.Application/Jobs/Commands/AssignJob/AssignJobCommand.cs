using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.AssignJob;

[RequiresPermission(Permission.JobsAssign)]
public sealed record AssignJobCommand(Guid JobId, Guid TenantUserId) : IRequest;

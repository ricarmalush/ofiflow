using MediatR;

namespace OfiFlow.Application.Jobs.Commands.AssignJob;

public sealed record AssignJobCommand(Guid JobId, Guid TenantUserId) : IRequest;

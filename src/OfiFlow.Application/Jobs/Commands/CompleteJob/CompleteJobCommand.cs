using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.CompleteJob;

[RequiresPermission(Permission.JobsExecute)]
public sealed record CompleteJobCommand(Guid Id) : IRequest;

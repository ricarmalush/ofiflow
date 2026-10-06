using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.StartJob;

[RequiresPermission(Permission.JobsExecute)]
public sealed record StartJobCommand(Guid Id) : IRequest;

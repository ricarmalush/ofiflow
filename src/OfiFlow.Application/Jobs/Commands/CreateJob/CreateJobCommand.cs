using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.CreateJob;

[RequiresPermission(Permission.JobsWrite)]
public sealed record CreateJobCommand(Guid CustomerId, string Title, string? Description, JobPriority Priority) : IRequest<Guid>;

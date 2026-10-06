using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.UpdateJob;

[RequiresPermission(Permission.JobsWrite)]
public sealed record UpdateJobCommand(Guid Id, string Title, string? Description, JobPriority Priority) : IRequest;

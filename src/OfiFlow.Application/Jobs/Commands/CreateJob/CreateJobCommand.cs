using MediatR;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.CreateJob;

public sealed record CreateJobCommand(Guid CustomerId, string Title, string? Description, JobPriority Priority) : IRequest<Guid>;

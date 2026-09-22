using MediatR;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.UpdateJob;

public sealed record UpdateJobCommand(Guid Id, string Title, string? Description, JobPriority Priority) : IRequest;

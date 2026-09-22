using MediatR;

namespace OfiFlow.Application.Jobs.Queries.GetJob;

public sealed record GetJobQuery(Guid Id) : IRequest<JobDto?>;

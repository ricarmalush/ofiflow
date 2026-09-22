using MediatR;

namespace OfiFlow.Application.Jobs.Queries.GetJobs;

public sealed record GetJobsQuery : IRequest<List<JobDto>>;

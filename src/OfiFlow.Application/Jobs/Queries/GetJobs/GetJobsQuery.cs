using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Queries.GetJobs;

[RequiresPermission(Permission.JobsRead)]
public sealed record GetJobsQuery : IRequest<List<JobDto>>;

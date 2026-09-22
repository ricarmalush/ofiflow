using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Jobs.Queries.GetJobs;

public sealed class GetJobsQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetJobsQuery, List<JobDto>>
{
    public Task<List<JobDto>> Handle(GetJobsQuery request, CancellationToken cancellationToken)
    {
        return dbContext.Jobs
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new JobDto(
                j.Id,
                j.CustomerId,
                j.Title,
                j.Description,
                j.Status.ToString(),
                j.Priority.ToString(),
                j.ScheduledDate,
                j.AssignedTenantUserId,
                j.EstimatedDurationMinutes))
            .ToListAsync(cancellationToken);
    }
}

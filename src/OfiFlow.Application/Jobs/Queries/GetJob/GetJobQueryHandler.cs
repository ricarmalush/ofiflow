using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Jobs.Queries.GetJob;

public sealed class GetJobQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetJobQuery, JobDto?>
{
    public Task<JobDto?> Handle(GetJobQuery request, CancellationToken cancellationToken)
    {
        return dbContext.Jobs
            .Where(j => j.Id == request.Id)
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
            .FirstOrDefaultAsync(cancellationToken);
    }
}

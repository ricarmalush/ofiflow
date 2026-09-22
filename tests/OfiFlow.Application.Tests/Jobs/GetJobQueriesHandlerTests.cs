using OfiFlow.Application.Jobs.Queries.GetJob;
using OfiFlow.Application.Jobs.Queries.GetJobs;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Jobs;

public class GetJobQueriesHandlerTests
{
    [Fact]
    public async Task GetJobQuery_ReturnsDto_WhenJobExists()
    {
        await using var db = TestDbContextFactory.Create();
        var job = Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await new GetJobQueryHandler(db).Handle(new GetJobQuery(job.Id), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Reparar fuga", result!.Title);
        Assert.Equal("New", result.Status);
    }

    [Fact]
    public async Task GetJobQuery_ReturnsNull_WhenJobDoesNotExist()
    {
        await using var db = TestDbContextFactory.Create();

        var result = await new GetJobQueryHandler(db).Handle(new GetJobQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetJobsQuery_ReturnsAllJobs()
    {
        await using var db = TestDbContextFactory.Create();
        db.Jobs.Add(Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Job 1", null, JobPriority.Normal));
        db.Jobs.Add(Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Job 2", null, JobPriority.High));
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await new GetJobsQueryHandler(db).Handle(new GetJobsQuery(), CancellationToken.None);

        Assert.Equal(2, result.Count);
    }
}

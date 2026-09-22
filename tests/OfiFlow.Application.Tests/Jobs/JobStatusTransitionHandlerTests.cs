using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Jobs.Commands.CancelJob;
using OfiFlow.Application.Jobs.Commands.CompleteJob;
using OfiFlow.Application.Jobs.Commands.StartJob;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Jobs;

public class JobStatusTransitionHandlerTests
{
    private static async Task<(TestDbContext Db, Job Job)> SeedJobAsync()
    {
        var db = TestDbContextFactory.Create();
        var job = Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        return (db, job);
    }

    [Fact]
    public async Task StartJob_FromNew_TransitionsToInProgress()
    {
        var (db, job) = await SeedJobAsync();
        await using var _ = db;

        await new StartJobCommandHandler(db).Handle(new StartJobCommand(job.Id), CancellationToken.None);

        var updated = await db.Jobs.FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.InProgress, updated.Status);
    }

    [Fact]
    public async Task CompleteJob_WhenNotInProgress_ThrowsBusinessRuleException()
    {
        var (db, job) = await SeedJobAsync();
        await using var _ = db;

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new CompleteJobCommandHandler(db).Handle(new CompleteJobCommand(job.Id), CancellationToken.None));
    }

    [Fact]
    public async Task CancelJob_FromNew_TransitionsToCancelled()
    {
        var (db, job) = await SeedJobAsync();
        await using var _ = db;

        await new CancelJobCommandHandler(db).Handle(new CancelJobCommand(job.Id), CancellationToken.None);

        var updated = await db.Jobs.FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.Cancelled, updated.Status);
    }

    [Fact]
    public async Task StartJob_WithUnknownId_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new StartJobCommandHandler(db).Handle(new StartJobCommand(Guid.NewGuid()), CancellationToken.None));
    }
}

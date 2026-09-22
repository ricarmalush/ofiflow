using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Jobs.Commands.UpdateJob;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Jobs;

public class UpdateJobCommandHandlerTests
{
    [Fact]
    public async Task Handle_UpdatesTitleDescriptionAndPriority()
    {
        await using var db = TestDbContextFactory.Create();
        var job = Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        await new UpdateJobCommandHandler(db).Handle(
            new UpdateJobCommand(job.Id, "Reparar fuga urgente", "Actualizado", JobPriority.Urgent),
            CancellationToken.None);

        var updated = await db.Jobs.FirstAsync(j => j.Id == job.Id);
        Assert.Equal("Reparar fuga urgente", updated.Title);
        Assert.Equal(JobPriority.Urgent, updated.Priority);
    }

    [Fact]
    public async Task Handle_WithUnknownId_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new UpdateJobCommandHandler(db).Handle(
                new UpdateJobCommand(Guid.NewGuid(), "Título", null, JobPriority.Normal),
                CancellationToken.None));
    }
}

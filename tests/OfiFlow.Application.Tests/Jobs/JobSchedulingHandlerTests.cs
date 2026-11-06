using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Jobs.Commands.ScheduleJob;
using OfiFlow.Application.Jobs.Commands.StartJob;
using OfiFlow.Application.Jobs.Commands.UnscheduleJob;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Jobs;

/// <summary>
/// Los dos comandos de la agenda (spec 020). Cada comprobación de lo guardado lee con OTRO contexto sobre la misma base: así se prueba
/// lo que quedó de verdad escrito y no lo que el primer contexto tiene en memoria.
/// </summary>
public class JobSchedulingHandlerTests
{
    private static async Task<(TestDbContext Db, Job Job)> SeedAsync(Action<Job>? prepare = null)
    {
        var db = TestDbContextFactory.Create();
        var job = Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        prepare?.Invoke(job);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        return (db, job);
    }

    private static async Task<Job> ReloadAsync(TestDbContext db, Guid id)
    {
        await using var other = TestDbContextFactory.CreateSharing(db);
        return await other.Jobs.AsNoTracking().FirstAsync(j => j.Id == id);
    }

    // ---- Programar ----

    [Fact]
    public async Task Schedule_FromNew_StoresTheInstantInUtc_AndTheDuration()
    {
        var (db, job) = await SeedAsync();
        await using var _ = db;

        await new ScheduleJobCommandHandler(db).Handle(new ScheduleJobCommand(job.Id, "2026-11-10T09:00:00+01:00", 90), CancellationToken.None);

        var stored = await ReloadAsync(db, job.Id);
        Assert.Equal(JobStatus.Scheduled, stored.Status);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), stored.ScheduledDate);
        Assert.Equal(90, stored.EstimatedDurationMinutes);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task Reschedule_WithoutDuration_ReplacesEverything()
    {
        var (db, job) = await SeedAsync(j => j.Schedule(new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc), 60));
        await using var _ = db;

        await new ScheduleJobCommandHandler(db).Handle(new ScheduleJobCommand(job.Id, "2026-11-12T15:00:00Z", null), CancellationToken.None);

        var stored = await ReloadAsync(db, job.Id);
        Assert.Equal(new DateTime(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc), stored.ScheduledDate);
        Assert.Null(stored.EstimatedDurationMinutes);
    }

    [Fact]
    public async Task Schedule_WithUnknownId_ThrowsNotFound_AndSavesNothing()
    {
        await using var db = TestDbContextFactory.Create();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            new ScheduleJobCommandHandler(db).Handle(new ScheduleJobCommand(Guid.NewGuid(), "2026-11-10T09:00:00Z", null), CancellationToken.None));

        Assert.Equal(JobErrors.NotFound, exception.Code);
        Assert.Equal(0, db.SaveCount);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Schedule_WhenTheStateDoesNotAllowIt_ThrowsItsCode_AndSavesNothing(JobStatus status)
    {
        var (db, job) = await SeedAsync(j =>
        {
            switch (status)
            {
                case JobStatus.InProgress:
                    j.Start();
                    break;
                case JobStatus.Completed:
                    j.Start();
                    j.Complete();
                    break;
                default:
                    j.Cancel();
                    break;
            }
        });
        await using var _ = db;

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            new ScheduleJobCommandHandler(db).Handle(new ScheduleJobCommand(job.Id, "2026-11-10T09:00:00Z", 30), CancellationToken.None));

        Assert.Equal(JobErrors.CannotSchedule, exception.Code);
        Assert.Equal(0, db.SaveCount);
        var stored = await ReloadAsync(db, job.Id);
        Assert.Equal(status, stored.Status);
        Assert.Null(stored.ScheduledDate);
    }

    [Fact]
    public async Task Schedule_WithoutPassingThroughTheValidator_RefusesADateWithoutOffset()
    {
        var (db, job) = await SeedAsync();
        await using var _ = db;

        await Assert.ThrowsAsync<FormatException>(() =>
            new ScheduleJobCommandHandler(db).Handle(new ScheduleJobCommand(job.Id, "2026-11-10T09:00:00", null), CancellationToken.None));

        Assert.Equal(0, db.SaveCount);
        Assert.Equal(JobStatus.New, (await ReloadAsync(db, job.Id)).Status);
    }

    // ---- Desprogramar ----

    [Fact]
    public async Task Unschedule_FromScheduled_GoesBackToNew_WithoutDateOrDuration()
    {
        var (db, job) = await SeedAsync(j => j.Schedule(new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc), 60));
        await using var _ = db;

        await new UnscheduleJobCommandHandler(db).Handle(new UnscheduleJobCommand(job.Id), CancellationToken.None);

        var stored = await ReloadAsync(db, job.Id);
        Assert.Equal(JobStatus.New, stored.Status);
        Assert.Null(stored.ScheduledDate);
        Assert.Null(stored.EstimatedDurationMinutes);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task Unschedule_FromNew_ThrowsItsCode_AndSavesNothing()
    {
        var (db, job) = await SeedAsync();
        await using var _ = db;

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            new UnscheduleJobCommandHandler(db).Handle(new UnscheduleJobCommand(job.Id), CancellationToken.None));

        Assert.Equal(JobErrors.CannotUnschedule, exception.Code);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task Unschedule_WithUnknownId_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            new UnscheduleJobCommandHandler(db).Handle(new UnscheduleJobCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(JobErrors.NotFound, exception.Code);
    }

    // ---- Iniciar un trabajo programado (R8) ----

    [Fact]
    public async Task Start_FromScheduled_TransitionsToInProgress_AndKeepsTheDate()
    {
        var date = new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
        var (db, job) = await SeedAsync(j => j.Schedule(date, 60));
        await using var _ = db;

        await new StartJobCommandHandler(db).Handle(new StartJobCommand(job.Id), CancellationToken.None);

        var stored = await ReloadAsync(db, job.Id);
        Assert.Equal(JobStatus.InProgress, stored.Status);
        Assert.Equal(date, stored.ScheduledDate);
        Assert.Equal(60, stored.EstimatedDurationMinutes);
    }
}

using OfiFlow.Domain.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Domain.Tests.Jobs;

/// <summary>
/// Programar, reprogramar y desprogramar un trabajo, y qué cambia de <c>Start</c> (spec 020 R1 a R10). Las guardas de estado son lo
/// que más importa: cada estado desde el que no se puede tiene su caso, y el trabajo no cambia cuando la operación falla.
/// </summary>
public class JobSchedulingTests
{
    private static readonly DateTime Monday9 = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tuesday10 = new(2026, 11, 10, 10, 30, 0, DateTimeKind.Utc);

    private static Job NewJob() => Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);

    private static Job JobIn(JobStatus status)
    {
        var job = NewJob();
        switch (status)
        {
            case JobStatus.New:
                break;
            case JobStatus.Scheduled:
                job.Schedule(Monday9, 60);
                break;
            case JobStatus.InProgress:
                job.Start();
                break;
            case JobStatus.Completed:
                job.Start();
                job.Complete();
                break;
            case JobStatus.Cancelled:
                job.Cancel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Este estado no se alcanza desde fuera.");
        }

        Assert.Equal(status, job.Status);
        return job;
    }

    // ---- Programar (R1, R2, R3) ----

    [Fact]
    public void Schedule_FromNew_SetsDateDurationAndStatus()
    {
        var job = NewJob();

        job.Schedule(Monday9, 90);

        Assert.Equal(JobStatus.Scheduled, job.Status);
        Assert.Equal(Monday9, job.ScheduledDate);
        Assert.Equal(DateTimeKind.Utc, job.ScheduledDate!.Value.Kind);
        Assert.Equal(90, job.EstimatedDurationMinutes);
    }

    [Fact]
    public void Schedule_WithoutDuration_LeavesItEmpty()
    {
        var job = NewJob();

        job.Schedule(Monday9, null);

        Assert.Equal(JobStatus.Scheduled, job.Status);
        Assert.Null(job.EstimatedDurationMinutes);
    }

    [Fact]
    public void Schedule_FromScheduled_ReplacesTheWholeProgramming()
    {
        var job = JobIn(JobStatus.Scheduled);

        job.Schedule(Tuesday10, 45);

        Assert.Equal(JobStatus.Scheduled, job.Status);
        Assert.Equal(Tuesday10, job.ScheduledDate);
        Assert.Equal(45, job.EstimatedDurationMinutes);
    }

    [Fact]
    public void Reschedule_WithoutDuration_ErasesTheOldDuration_ItIsNotKept()
    {
        var job = JobIn(JobStatus.Scheduled);
        Assert.Equal(60, job.EstimatedDurationMinutes);

        job.Schedule(Tuesday10, null);

        Assert.Equal(Tuesday10, job.ScheduledDate);
        Assert.Null(job.EstimatedDurationMinutes);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public void Schedule_FromAnyOtherState_Throws_AndChangesNothing(JobStatus status)
    {
        var job = JobIn(status);
        var dateBefore = job.ScheduledDate;
        var durationBefore = job.EstimatedDurationMinutes;

        var exception = Assert.Throws<DomainException>(() => job.Schedule(Tuesday10, 30));

        Assert.Equal(JobErrors.CannotSchedule, exception.Code);
        Assert.Equal(status, exception.Arguments[0]);
        Assert.Equal(status, job.Status);
        Assert.Equal(dateBefore, job.ScheduledDate);
        Assert.Equal(durationBefore, job.EstimatedDurationMinutes);
    }

    // ---- Cotas (R4, R5, R6) ----

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Schedule_WithADateThatIsNotUtc_Throws_ItIsNeverReadAsUtcByGuessing(DateTimeKind kind)
    {
        var job = NewJob();
        var notUtc = DateTime.SpecifyKind(Monday9, kind);

        Assert.Throws<ArgumentOutOfRangeException>(() => job.Schedule(notUtc, null));

        Assert.Equal(JobStatus.New, job.Status);
        Assert.Null(job.ScheduledDate);
    }

    [Fact]
    public void ScheduledDate_TheLowerBoundIsIncluded_AndOneTickBeforeIsRejected()
    {
        var job = NewJob();

        Assert.Throws<ArgumentOutOfRangeException>(() => job.Schedule(Job.ScheduledDateMin.AddTicks(-1), null));
        job.Schedule(Job.ScheduledDateMin, null);

        Assert.Equal(Job.ScheduledDateMin, job.ScheduledDate);
    }

    [Fact]
    public void ScheduledDate_TheUpperBoundIsExcluded_AndOneTickBeforeIsAccepted()
    {
        var job = NewJob();

        Assert.Throws<ArgumentOutOfRangeException>(() => job.Schedule(Job.ScheduledDateMax, null));
        job.Schedule(Job.ScheduledDateMax.AddTicks(-1), null);

        Assert.Equal(Job.ScheduledDateMax.AddTicks(-1), job.ScheduledDate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public void Duration_TheLimitsThemselvesAreAccepted(int minutes)
    {
        var job = NewJob();

        job.Schedule(Monday9, minutes);

        Assert.Equal(minutes, job.EstimatedDurationMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public void Duration_OutsideTheLimits_Throws_AndChangesNothing(int minutes)
    {
        var job = NewJob();

        Assert.Throws<ArgumentOutOfRangeException>(() => job.Schedule(Monday9, minutes));

        Assert.Equal(JobStatus.New, job.Status);
        Assert.Null(job.ScheduledDate);
    }

    // ---- Desprogramar (R7) ----

    [Fact]
    public void Unschedule_FromScheduled_GoesBackToNew_AndErasesDateAndDuration()
    {
        var job = JobIn(JobStatus.Scheduled);

        job.Unschedule();

        Assert.Equal(JobStatus.New, job.Status);
        Assert.Null(job.ScheduledDate);
        Assert.Null(job.EstimatedDurationMinutes);
    }

    [Theory]
    [InlineData(JobStatus.New)]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public void Unschedule_FromAnyOtherState_Throws(JobStatus status)
    {
        var job = JobIn(status);

        var exception = Assert.Throws<DomainException>(job.Unschedule);

        Assert.Equal(JobErrors.CannotUnschedule, exception.Code);
        Assert.Equal(status, exception.Arguments[0]);
        Assert.Equal(status, job.Status);
    }

    [Fact]
    public void AnUnscheduledJob_CanBeScheduledAgain()
    {
        var job = JobIn(JobStatus.Scheduled);
        job.Unschedule();

        job.Schedule(Tuesday10, 20);

        Assert.Equal(JobStatus.Scheduled, job.Status);
        Assert.Equal(Tuesday10, job.ScheduledDate);
    }

    // ---- Iniciar, completar, cancelar (R8, R9) ----

    [Fact]
    public void Start_FromScheduled_TransitionsToInProgress_AndKeepsTheDate()
    {
        var job = JobIn(JobStatus.Scheduled);

        job.Start();

        Assert.Equal(JobStatus.InProgress, job.Status);
        Assert.Equal(Monday9, job.ScheduledDate);
        Assert.Equal(60, job.EstimatedDurationMinutes);
    }

    [Fact]
    public void Start_FromNew_StillWorks_AJobDoesNotHaveToBeScheduled()
    {
        var job = JobIn(JobStatus.New);

        job.Start();

        Assert.Equal(JobStatus.InProgress, job.Status);
        Assert.Null(job.ScheduledDate);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public void Start_FromAnyOtherState_StillThrows(JobStatus status)
    {
        var job = JobIn(status);

        var exception = Assert.Throws<DomainException>(job.Start);

        Assert.Equal(JobErrors.CannotStart, exception.Code);
        Assert.Equal(status, exception.Arguments[0]);
    }

    [Fact]
    public void TheWholeLifeCycle_NewScheduledInProgressCompleted_Works()
    {
        var job = NewJob();

        job.Schedule(Monday9, 120);
        job.Start();
        job.Complete();

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(Monday9, job.ScheduledDate);
    }

    [Fact]
    public void Cancel_FromScheduled_Cancels_AndKeepsTheDate()
    {
        var job = JobIn(JobStatus.Scheduled);

        job.Cancel();

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(Monday9, job.ScheduledDate);
    }

    // ---- Independencia (R10) ----

    [Fact]
    public void UpdateDetails_AndAssign_DoNotTouchTheProgramming()
    {
        var job = JobIn(JobStatus.Scheduled);

        job.UpdateDetails("Otro título", "Otra descripción", JobPriority.Urgent);
        job.AssignTo(Guid.NewGuid());

        Assert.Equal(JobStatus.Scheduled, job.Status);
        Assert.Equal(Monday9, job.ScheduledDate);
        Assert.Equal(60, job.EstimatedDurationMinutes);
    }

    [Fact]
    public void Schedule_DoesNotRequireAnAssignee_AndAssigningDoesNotRequireASchedule()
    {
        var unassigned = NewJob();
        unassigned.Schedule(Monday9, null);
        Assert.Null(unassigned.AssignedTenantUserId);

        var unscheduled = NewJob();
        unscheduled.AssignTo(Guid.NewGuid());
        Assert.Equal(JobStatus.New, unscheduled.Status);
        Assert.Null(unscheduled.ScheduledDate);
    }
}

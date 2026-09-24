using OfiFlow.Domain.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Domain.Tests.Jobs;

public class JobTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid CustomerId = Guid.NewGuid();

    private static Job CreateJob() => Job.Create(TenantId, CustomerId, "Reparar fuga", "Debajo del fregadero", JobPriority.Normal);

    [Fact]
    public void Create_WithValidTitle_StartsInNewStatus()
    {
        var job = CreateJob();

        Assert.Equal(TenantId, job.TenantId);
        Assert.Equal(CustomerId, job.CustomerId);
        Assert.Equal(JobStatus.New, job.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutTitle_Throws(string title)
    {
        var exception = Assert.Throws<DomainException>(() => Job.Create(TenantId, CustomerId, title, null, JobPriority.Normal));

        Assert.Equal(JobErrors.TitleRequired, exception.Code);
    }

    [Fact]
    public void Start_FromNew_TransitionsToInProgress()
    {
        var job = CreateJob();

        job.Start();

        Assert.Equal(JobStatus.InProgress, job.Status);
    }

    [Fact]
    public void Start_WhenNotNew_Throws()
    {
        var job = CreateJob();
        job.Start();

        var exception = Assert.Throws<DomainException>(job.Start);

        Assert.Equal(JobErrors.CannotStart, exception.Code);
        Assert.Equal(JobStatus.InProgress, exception.Arguments[0]);
    }

    [Fact]
    public void Complete_FromInProgress_TransitionsToCompleted()
    {
        var job = CreateJob();
        job.Start();

        job.Complete();

        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Fact]
    public void Complete_WhenNotInProgress_Throws()
    {
        var job = CreateJob();

        var exception = Assert.Throws<DomainException>(job.Complete);

        Assert.Equal(JobErrors.CannotComplete, exception.Code);
        Assert.Equal(JobStatus.New, exception.Arguments[0]);
    }

    [Fact]
    public void Cancel_FromNew_TransitionsToCancelled()
    {
        var job = CreateJob();

        job.Cancel();

        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void Cancel_FromInProgress_TransitionsToCancelled()
    {
        var job = CreateJob();
        job.Start();

        job.Cancel();

        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void Cancel_WhenCompleted_Throws()
    {
        var job = CreateJob();
        job.Start();
        job.Complete();

        var exception = Assert.Throws<DomainException>(job.Cancel);

        Assert.Equal(JobErrors.CannotCancelCompleted, exception.Code);
    }

    [Fact]
    public void AssignTo_SetsAssignedTenantUserId()
    {
        var job = CreateJob();
        var tenantUserId = Guid.NewGuid();

        job.AssignTo(tenantUserId);

        Assert.Equal(tenantUserId, job.AssignedTenantUserId);
    }

    [Fact]
    public void Unassign_ClearsAssignedTenantUserId()
    {
        var job = CreateJob();
        job.AssignTo(Guid.NewGuid());

        job.Unassign();

        Assert.Null(job.AssignedTenantUserId);
    }

    [Fact]
    public void UpdateDetails_ChangesTitleDescriptionAndPriority()
    {
        var job = CreateJob();

        job.UpdateDetails("Reparar fuga urgente", "Actualizado", JobPriority.Urgent);

        Assert.Equal("Reparar fuga urgente", job.Title);
        Assert.Equal("Actualizado", job.Description);
        Assert.Equal(JobPriority.Urgent, job.Priority);
    }

    [Fact]
    public void UpdateDetails_WithoutTitle_Throws()
    {
        var job = CreateJob();

        var exception = Assert.Throws<DomainException>(() => job.UpdateDetails("", null, JobPriority.Normal));

        Assert.Equal(JobErrors.TitleRequired, exception.Code);
    }
}

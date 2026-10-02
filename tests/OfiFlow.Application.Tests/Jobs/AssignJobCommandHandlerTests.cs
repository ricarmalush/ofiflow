using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Jobs.Commands.AssignJob;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Jobs;

public class AssignJobCommandHandlerTests
{
    [Fact]
    public async Task Handle_WithTenantUserInSameTenant_AssignsJob()
    {
        await using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var job = Job.Create(tenantId, Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        var tenantUser = TenantUser.CreateOwner(tenantId, Guid.NewGuid());
        db.Jobs.Add(job);
        db.TenantUsers.Add(tenantUser);
        await db.SaveChangesAsync(CancellationToken.None);

        await new AssignJobCommandHandler(db).Handle(new AssignJobCommand(job.Id, tenantUser.Id), CancellationToken.None);

        var updated = await db.Jobs.FirstAsync(j => j.Id == job.Id);
        Assert.Equal(tenantUser.Id, updated.AssignedTenantUserId);
    }

    [Fact]
    public async Task Handle_WithUnknownTenantUser_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();
        var job = Job.Create(Guid.NewGuid(), Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            new AssignJobCommandHandler(db).Handle(new AssignJobCommand(job.Id, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantUserNotFound, exception.Code);
    }
}

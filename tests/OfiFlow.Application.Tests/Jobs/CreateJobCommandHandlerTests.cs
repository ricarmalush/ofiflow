using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Jobs.Commands.CreateJob;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Jobs;

public class CreateJobCommandHandlerTests
{
    [Fact]
    public async Task Handle_WithExistingCustomer_CreatesJob()
    {
        await using var db = TestDbContextFactory.Create();
        var tenantContext = new FakeTenantContext(Guid.NewGuid());
        var customer = Customer.Create(tenantContext.TenantId, CustomerType.Person, "Juan Pérez", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new CreateJobCommandHandler(db, tenantContext);
        var jobId = await handler.Handle(
            new CreateJobCommand(customer.Id, "Reparar fuga", "Debajo del fregadero", JobPriority.Normal),
            CancellationToken.None);

        var job = await db.Jobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal(tenantContext.TenantId, job.TenantId);
        Assert.Equal(customer.Id, job.CustomerId);
        Assert.Equal(JobStatus.New, job.Status);
    }

    [Fact]
    public async Task Handle_WithUnknownCustomer_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new CreateJobCommandHandler(db, new FakeTenantContext(Guid.NewGuid()));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(new CreateJobCommand(Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal), CancellationToken.None));
    }
}

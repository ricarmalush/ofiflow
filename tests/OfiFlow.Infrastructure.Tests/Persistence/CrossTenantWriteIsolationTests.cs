using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Customers.Commands.DeleteCustomer;
using OfiFlow.Application.Customers.Commands.UpdateCustomer;
using OfiFlow.Application.Jobs.Commands.AssignJob;
using OfiFlow.Application.Jobs.Commands.CancelJob;
using OfiFlow.Application.Jobs.Commands.CompleteJob;
using OfiFlow.Application.Jobs.Commands.StartJob;
using OfiFlow.Application.Jobs.Commands.UpdateJob;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Spec 007: la empresa B no puede modificar, borrar, cambiar de estado ni asignar nada de la
/// empresa A, aunque conozca el Id. Se ejecutan los handlers reales con el filtro real contra
/// SQL Server real (ADR-008). Cada test comprueba dos cosas: que la operación responde "no
/// encontrado" (igual que si el Id no existiera, ADR-002) y que el dato de A sigue intacto.
/// Cada test usa empresas nuevas (Guid), así que comparten el contenedor sin interferirse.
/// </summary>
public class CrossTenantWriteIsolationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    // --- Trabajos de A, atacados desde B ---

    [Fact]
    public async Task UpdateJob_WithAnotherTenantsId_IsNotFound_AndTheJobIsUntouched()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var jobId = await SeedJobAsync(tenantA);

        var exception = await AsTenantAsync(tenantB, db =>
            new UpdateJobCommandHandler(db).Handle(new UpdateJobCommand(jobId, "Cambiado por B", "x", JobPriority.Urgent), default));

        Assert.Equal(JobErrors.NotFound, exception.Code);

        var job = await ReadJobAsync(tenantA, jobId);
        Assert.Equal("Reparar fuga", job.Title);
        Assert.Equal(JobPriority.Normal, job.Priority);
    }

    [Fact]
    public async Task StartJob_WithAnotherTenantsId_IsNotFound_AndTheJobStaysNew()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var jobId = await SeedJobAsync(tenantA);

        var exception = await AsTenantAsync(tenantB, db =>
            new StartJobCommandHandler(db).Handle(new StartJobCommand(jobId), default));

        Assert.Equal(JobErrors.NotFound, exception.Code);
        Assert.Equal(JobStatus.New, (await ReadJobAsync(tenantA, jobId)).Status);
    }

    [Fact]
    public async Task CompleteJob_WithAnotherTenantsId_IsNotFound_AndTheJobStaysInProgress()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        // En curso: si B lo encontrara, completarlo funcionaría. Por eso se siembra así.
        var jobId = await SeedJobAsync(tenantA, started: true);

        var exception = await AsTenantAsync(tenantB, db =>
            new CompleteJobCommandHandler(db).Handle(new CompleteJobCommand(jobId), default));

        Assert.Equal(JobErrors.NotFound, exception.Code);
        Assert.Equal(JobStatus.InProgress, (await ReadJobAsync(tenantA, jobId)).Status);
    }

    [Fact]
    public async Task CancelJob_WithAnotherTenantsId_IsNotFound_AndTheJobIsNotCancelled()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var jobId = await SeedJobAsync(tenantA);

        var exception = await AsTenantAsync(tenantB, db =>
            new CancelJobCommandHandler(db).Handle(new CancelJobCommand(jobId), default));

        Assert.Equal(JobErrors.NotFound, exception.Code);
        Assert.Equal(JobStatus.New, (await ReadJobAsync(tenantA, jobId)).Status);
    }

    [Fact]
    public async Task AssignJob_WithAnotherTenantsJob_IsNotFound_AndTheJobStaysUnassigned()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var jobId = await SeedJobAsync(tenantA);
        var tenantUserOfB = await SeedTenantUserAsync(tenantB);

        var exception = await AsTenantAsync(tenantB, db =>
            new AssignJobCommandHandler(db).Handle(new AssignJobCommand(jobId, tenantUserOfB), default));

        Assert.Equal(JobErrors.NotFound, exception.Code);
        Assert.Null((await ReadJobAsync(tenantA, jobId)).AssignedTenantUserId);
    }

    [Fact]
    public async Task AssignJob_ToAnotherTenantsUser_IsNotFound_AndTheJobStaysUnassigned()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var ownJobId = await SeedJobAsync(tenantB);
        var tenantUserOfA = await SeedTenantUserAsync(tenantA);

        var exception = await AsTenantAsync(tenantB, db =>
            new AssignJobCommandHandler(db).Handle(new AssignJobCommand(ownJobId, tenantUserOfA), default));

        Assert.Equal(TenancyErrors.TenantUserNotFound, exception.Code);
        Assert.Null((await ReadJobAsync(tenantB, ownJobId)).AssignedTenantUserId);
    }

    // --- Clientes de A, atacados desde B ---

    [Fact]
    public async Task UpdateCustomer_WithAnotherTenantsId_IsNotFound_AndTheCustomerIsUntouched()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var customerId = await SeedCustomerAsync(tenantA);

        var exception = await AsTenantAsync(tenantB, db =>
            new UpdateCustomerCommandHandler(db).Handle(
                new UpdateCustomerCommand(customerId, "Cambiado por B", null, null, null, null), default));

        Assert.Equal(CustomerErrors.NotFound, exception.Code);
        Assert.Equal("Cliente A", (await ReadCustomerAsync(tenantA, customerId)).Name);
    }

    [Fact]
    public async Task DeleteCustomer_WithAnotherTenantsId_IsNotFound_AndTheCustomerStillExists()
    {
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var customerId = await SeedCustomerAsync(tenantA);

        var exception = await AsTenantAsync(tenantB, db =>
            new DeleteCustomerCommandHandler(db).Handle(new DeleteCustomerCommand(customerId), default));

        Assert.Equal(CustomerErrors.NotFound, exception.Code);
        Assert.Equal("Cliente A", (await ReadCustomerAsync(tenantA, customerId)).Name);
    }

    // --- Controles positivos: las mismas operaciones SÍ funcionan para la empresa dueña ---
    // Sin ellos, "no encontrado" podría deberse a un error al sembrar los datos y no al aislamiento.

    [Fact]
    public async Task StartJob_AsTheOwnerTenant_Succeeds()
    {
        var tenantA = Guid.NewGuid();
        var jobId = await SeedJobAsync(tenantA);

        await using (var db = fixture.CreateDbContext(tenantA))
        {
            await new StartJobCommandHandler(db).Handle(new StartJobCommand(jobId), default);
        }

        Assert.Equal(JobStatus.InProgress, (await ReadJobAsync(tenantA, jobId)).Status);
    }

    [Fact]
    public async Task UpdateCustomer_AsTheOwnerTenant_Succeeds()
    {
        var tenantA = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantA);

        await using (var db = fixture.CreateDbContext(tenantA))
        {
            await new UpdateCustomerCommandHandler(db).Handle(
                new UpdateCustomerCommand(customerId, "Cliente A actualizado", null, null, null, null), default);
        }

        Assert.Equal("Cliente A actualizado", (await ReadCustomerAsync(tenantA, customerId)).Name);
    }

    [Fact]
    public async Task DeleteCustomer_AsTheOwnerTenant_Succeeds()
    {
        var tenantA = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantA);

        await using (var db = fixture.CreateDbContext(tenantA))
        {
            await new DeleteCustomerCommandHandler(db).Handle(new DeleteCustomerCommand(customerId), default);
        }

        await using var check = fixture.CreateDbContext(tenantA);
        Assert.False(await check.Customers.AnyAsync(c => c.Id == customerId));
    }

    // --- Ayudas ---

    /// <summary>Ejecuta la acción como la empresa indicada y exige que falle con NotFoundException.</summary>
    private async Task<NotFoundException> AsTenantAsync(Guid tenantId, Func<ApplicationDbContext, Task> action)
    {
        await using var db = fixture.CreateDbContext(tenantId);

        return await Assert.ThrowsAsync<NotFoundException>(() => action(db));
    }

    private async Task<Guid> SeedCustomerAsync(Guid tenantId)
    {
        await using var db = fixture.CreateDbContext(tenantId);
        var customer = Customer.Create(tenantId, CustomerType.Person, "Cliente A", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);

        return customer.Id;
    }

    private async Task<Guid> SeedJobAsync(Guid tenantId, bool started = false)
    {
        await using var db = fixture.CreateDbContext(tenantId);
        var job = Job.Create(tenantId, Guid.NewGuid(), "Reparar fuga", "Gotea desde ayer", JobPriority.Normal);
        if (started)
        {
            job.Start();
        }

        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        return job.Id;
    }

    private async Task<Guid> SeedTenantUserAsync(Guid tenantId)
    {
        await using var db = fixture.CreateDbContext(tenantId);
        var tenantUser = TenantUser.Create(tenantId, Guid.NewGuid(), TenantRole.Technician);
        db.TenantUsers.Add(tenantUser);
        await db.SaveChangesAsync(CancellationToken.None);

        return tenantUser.Id;
    }

    private async Task<Job> ReadJobAsync(Guid tenantId, Guid jobId)
    {
        await using var db = fixture.CreateDbContext(tenantId);

        return await db.Jobs.SingleAsync(j => j.Id == jobId);
    }

    private async Task<Customer> ReadCustomerAsync(Guid tenantId, Guid customerId)
    {
        await using var db = fixture.CreateDbContext(tenantId);

        return await db.Customers.SingleAsync(c => c.Id == customerId);
    }
}

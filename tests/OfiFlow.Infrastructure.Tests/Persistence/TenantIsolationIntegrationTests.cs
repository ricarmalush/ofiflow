using Microsoft.EntityFrameworkCore;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// El test obligatorio de la sección 42 del prompt maestro — "un usuario del Tenant A nunca
/// puede acceder a información del Tenant B" — contra SQL Server real (LocalDB, ADR-008),
/// no InMemory. Verificado manualmente por HTTP en 001/002/003; esta es su automatización.
/// </summary>
public class TenantIsolationIntegrationTests : IAsyncLifetime
{
    private readonly LocalDbFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Customers_AreNotVisibleAcrossTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var dbAsTenantA = _fixture.CreateDbContext(tenantA))
        {
            dbAsTenantA.Customers.Add(Customer.Create(tenantA, CustomerType.Person, "Cliente A", null, null, null, null));
            await dbAsTenantA.SaveChangesAsync(CancellationToken.None);
        }

        await using var dbAsTenantB = _fixture.CreateDbContext(tenantB);
        var visibleToTenantB = await dbAsTenantB.Customers.ToListAsync();

        Assert.Empty(visibleToTenantB);
    }

    [Fact]
    public async Task Jobs_AreNotVisibleAcrossTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await using (var dbAsTenantA = _fixture.CreateDbContext(tenantA))
        {
            dbAsTenantA.Jobs.Add(Job.Create(tenantA, customerId, "Reparar fuga", null, JobPriority.Normal));
            await dbAsTenantA.SaveChangesAsync(CancellationToken.None);
        }

        await using var dbAsTenantB = _fixture.CreateDbContext(tenantB);
        var visibleToTenantB = await dbAsTenantB.Jobs.ToListAsync();

        Assert.Empty(visibleToTenantB);
    }
}

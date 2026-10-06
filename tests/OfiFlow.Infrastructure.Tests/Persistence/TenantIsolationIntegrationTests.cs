using Microsoft.EntityFrameworkCore;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// El test obligatorio de la sección 42 del prompt maestro — "un usuario del Tenant A nunca
/// puede acceder a información del Tenant B" — contra SQL Server real (Testcontainers, ADR-008),
/// no InMemory. Verificado manualmente por HTTP en 001/002/003; esta es su automatización.
/// </summary>
public class TenantIsolationIntegrationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    // Un contenedor compartido por todos los tests de la clase (cada test usa empresas nuevas).
    private readonly SqlServerContainerFixture _fixture = fixture;

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

    [Fact]
    public async Task TenantUsers_AreNotVisibleAcrossTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var dbAsTenantA = _fixture.CreateDbContext(tenantA))
        {
            dbAsTenantA.TenantUsers.Add(TenantUser.Create(tenantA, Guid.NewGuid(), TenantRole.Technician));
            await dbAsTenantA.SaveChangesAsync(CancellationToken.None);
        }

        await using var dbAsTenantB = _fixture.CreateDbContext(tenantB);
        var visibleToTenantB = await dbAsTenantB.TenantUsers.ToListAsync();

        Assert.Empty(visibleToTenantB);

        // Control positivo: la propia empresa sí los ve, así que "vacío" no se debe a un fallo al sembrar.
        await using var dbAsOwner = _fixture.CreateDbContext(tenantA);
        Assert.Single(await dbAsOwner.TenantUsers.ToListAsync());
    }
}

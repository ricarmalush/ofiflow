using Microsoft.EntityFrameworkCore;
using OfiFlow.Domain.Customers;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Sanity check (EF Core InMemory) that the reflection-based Global Query Filter from
/// ApplicationDbContext actually restricts by tenant. This is NOT the mandatory
/// tenant-isolation test against real SQL Server from specs/001-customer/tasks.md —
/// that one is pending the LocalDB/Testcontainers decision noted in ADR-006.
/// </summary>
public class ApplicationDbContextTenantFilterTests
{
    [Fact]
    public async Task Customers_OnlyReturnsRowsBelongingToTheActiveTenant()
    {
        var databaseName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        await using (var seedAsTenantA = new ApplicationDbContext(options, new FixedTenantContext(tenantA)))
        {
            seedAsTenantA.Customers.Add(Customer.Create(tenantA, CustomerType.Person, "Cliente A", null, null, null, null));
            await seedAsTenantA.SaveChangesAsync(CancellationToken.None);
        }

        await using (var seedAsTenantB = new ApplicationDbContext(options, new FixedTenantContext(tenantB)))
        {
            seedAsTenantB.Customers.Add(Customer.Create(tenantB, CustomerType.Person, "Cliente B", null, null, null, null));
            await seedAsTenantB.SaveChangesAsync(CancellationToken.None);
        }

        await using var readAsTenantA = new ApplicationDbContext(options, new FixedTenantContext(tenantA));
        var visibleCustomers = await readAsTenantA.Customers.ToListAsync();

        Assert.Single(visibleCustomers);
        Assert.Equal("Cliente A", visibleCustomers[0].Name);
    }
}

using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

/// <summary>El estado de suspensión de las empresas: la única lectura de la tabla de empresas para saberlo (spec 011).</summary>
public class TenantStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestDbContext _db = TestDbContextFactory.Create();

    private async Task<Tenant> AddTenantAsync(string name, bool suspended)
    {
        var tenant = Tenant.Create(name);
        if (suspended)
        {
            tenant.Suspend(Now);
        }

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(CancellationToken.None);

        return tenant;
    }

    [Fact]
    public async Task ASuspendedCompany_IsSuspended()
    {
        var tenant = await AddTenantAsync("Fontanería A", suspended: true);

        Assert.True(await _db.IsSuspendedAsync(tenant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AnActiveCompany_IsNotSuspended()
    {
        var tenant = await AddTenantAsync("Fontanería A", suspended: false);

        Assert.False(await _db.IsSuspendedAsync(tenant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ACompanyThatDoesNotExist_IsNotSuspended()
    {
        // No hay nada que cortar: la ausencia de la empresa no se confunde con una suspensión.
        Assert.False(await _db.IsSuspendedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task OnlyTheSuspendedCompanies_AppearInTheComposableQuery()
    {
        var suspended = await AddTenantAsync("Fontanería A", suspended: true);
        await AddTenantAsync("Electricidad B", suspended: false);
        var another = await AddTenantAsync("Pintura C", suspended: true);

        var ids = _db.SuspendedTenantIds().ToList();

        Assert.Equal(2, ids.Count);
        Assert.Contains(suspended.Id, ids);
        Assert.Contains(another.Id, ids);
    }

    [Fact]
    public async Task AReactivatedCompany_StopsBeingSuspended()
    {
        var tenant = await AddTenantAsync("Fontanería A", suspended: true);

        tenant.Reactivate();
        await _db.SaveChangesAsync(CancellationToken.None);

        Assert.False(await _db.IsSuspendedAsync(tenant.Id, CancellationToken.None));
    }
}

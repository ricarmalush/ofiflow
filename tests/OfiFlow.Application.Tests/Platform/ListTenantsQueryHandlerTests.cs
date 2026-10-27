using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Platform;
using OfiFlow.Application.Platform.Queries.ListTenants;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Platform;

/// <summary>
/// Listar las empresas (spec 011 R11): solo metadatos, paginado con un orden total y un filtro por suspendida. Que el recuento de
/// miembros de otras empresas cruce el filtro de tenant lo prueba contra SQL Server <c>ListTenantsPersistenceTests</c> (esta base en
/// memoria no tiene filtro de tenant, así que aquí solo se prueba la forma del resultado).
/// </summary>
public class ListTenantsQueryHandlerTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly ListLogger<ListTenantsQueryHandler> _logger = new();
    private readonly Guid _operatorId = Guid.NewGuid();

    private ListTenantsQueryHandler Handler(Guid? userId = null) =>
        new(_db, new FakeCurrentUser(role: null, userId: userId ?? _operatorId, platformSession: true, platformOperatorActive: true), _logger);

    private async Task<Tenant> AddTenantAsync(string name, int daysAfterStart, bool suspended = false, int members = 0)
    {
        var tenant = Tenant.Create(name);
        tenant.CreatedAt = Start.AddDays(daysAfterStart);
        if (suspended)
        {
            tenant.Suspend(Start.AddDays(daysAfterStart + 1));
        }

        _db.Tenants.Add(tenant);

        for (var index = 0; index < members; index++)
        {
            _db.TenantUsers.Add(TenantUser.Create(tenant.Id, Guid.NewGuid(), index == 0 ? TenantRole.Owner : TenantRole.Technician));
        }

        await _db.SaveChangesAsync(CancellationToken.None);

        return tenant;
    }

    [Fact]
    public async Task EachTenant_ComesWithExactlyItsNameDateMembersAndState()
    {
        var active = await AddTenantAsync("Fontanería A", daysAfterStart: 1, members: 3);
        var suspended = await AddTenantAsync("Electricidad B", daysAfterStart: 2, suspended: true, members: 1);

        var result = await Handler().Handle(new ListTenantsQuery(), CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        var a = Assert.Single(result.Items, t => t.Id == active.Id);
        Assert.Equal(("Fontanería A", Start.AddDays(1), 3, (DateTime?)null), (a.Name, a.CreatedAt, a.MemberCount, a.SuspendedAt));
        var b = Assert.Single(result.Items, t => t.Id == suspended.Id);
        Assert.Equal(("Electricidad B", 1, (DateTime?)Start.AddDays(3)), (b.Name, b.MemberCount, b.SuspendedAt));
    }

    [Fact]
    public void TheTenantShape_IsExactlyFiveFields_NothingPersonalCanBeAddedByAccident()
    {
        // R11 y D5: Id, nombre, alta, número de miembros y estado. Ni emails, ni nombres de personas, ni clientes, ni trabajos. Si alguien
        // añade una propiedad, este test falla y obliga a decidir, a propósito, si el operador puede verla.
        var properties = typeof(PlatformTenantDto).GetProperties().Select(property => property.Name).Order().ToList();

        Assert.Equal(["CreatedAt", "Id", "MemberCount", "Name", "SuspendedAt"], properties);
    }

    [Fact]
    public async Task ACompanyWithoutMembers_HasZero()
    {
        await AddTenantAsync("Vacía", daysAfterStart: 1, members: 0);

        var result = await Handler().Handle(new ListTenantsQuery(), CancellationToken.None);

        Assert.Equal(0, Assert.Single(result.Items).MemberCount);
    }

    [Fact]
    public async Task TheOrder_IsNewestFirst_AndTiesBreakById()
    {
        var oldest = await AddTenantAsync("Vieja", daysAfterStart: 1);

        // Las dos del mismo día se insertan en orden de Id DESCENDENTE: si el orden dependiera de la inserción y no del Id, saldrían al revés.
        Tenant first;
        Tenant second;
        do
        {
            first = await AddTenantAsync("Mismo día A", daysAfterStart: 5);
            second = await AddTenantAsync("Mismo día B", daysAfterStart: 5);
            if (first.Id.CompareTo(second.Id) > 0)
            {
                break;
            }

            _db.Tenants.RemoveRange(first, second);
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        while (true);

        var newest = await AddTenantAsync("Nueva", daysAfterStart: 9);

        var result = await Handler().Handle(new ListTenantsQuery(), CancellationToken.None);

        Assert.Equal([newest.Id, second.Id, first.Id, oldest.Id], result.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task ConsecutivePages_NeitherRepeatNorSkipAnyTenant()
    {
        // Doce empresas con solo cuatro fechas distintas: el desempate por Id es lo que hace el orden total y estable.
        for (var index = 0; index < 12; index++)
        {
            await AddTenantAsync($"Empresa {index}", daysAfterStart: index % 4);
        }

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await Handler().Handle(new ListTenantsQuery(Page: page, PageSize: 5), CancellationToken.None);
            Assert.Equal(12, result.TotalCount);
            seen.AddRange(result.Items.Select(t => t.Id));
        }

        Assert.Equal(12, seen.Count);
        Assert.Equal(12, seen.Distinct().Count());
    }

    [Fact]
    public async Task APageBeyondTheEnd_IsEmpty_ButKeepsTheTotal()
    {
        await AddTenantAsync("Única", daysAfterStart: 1);

        var result = await Handler().Handle(new ListTenantsQuery(Page: 3, PageSize: 10), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal((3, 10), (result.Page, result.PageSize));
    }

    [Fact]
    public async Task TheSuspendedFilter_SelectsOnlySuspended_OnlyActive_OrAll()
    {
        await AddTenantAsync("Activa 1", 1);
        await AddTenantAsync("Activa 2", 2);
        await AddTenantAsync("Suspendida", 3, suspended: true);

        var onlySuspended = await Handler().Handle(new ListTenantsQuery(Suspended: true), CancellationToken.None);
        var onlyActive = await Handler().Handle(new ListTenantsQuery(Suspended: false), CancellationToken.None);
        var all = await Handler().Handle(new ListTenantsQuery(Suspended: null), CancellationToken.None);

        Assert.Equal(["Suspendida"], onlySuspended.Items.Select(t => t.Name));
        Assert.Equal(1, onlySuspended.TotalCount);
        Assert.Equal(2, onlyActive.TotalCount);
        Assert.DoesNotContain(onlyActive.Items, t => t.SuspendedAt is not null);
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task Listing_LeavesTheEvent1603WithTheOperatorPageAndCount_AndNothingElse()
    {
        await AddTenantAsync("Fontanería A", 1);
        await AddTenantAsync("Electricidad B", 2);

        await Handler().Handle(new ListTenantsQuery(Page: 1, PageSize: 1), CancellationToken.None);

        var logged = Assert.Single(_logger.Entries);
        Assert.Equal(SecurityEventIds.PlatformTenantsListed, logged.EventId.Id);
        Assert.Contains(_operatorId.ToString(), logged.Message);
        // Solo Ids y números: ni el nombre de ninguna empresa.
        Assert.DoesNotContain("Fontanería", logged.Message);
        Assert.DoesNotContain("Electricidad", logged.Message);
    }

    [Fact]
    public async Task Listing_ChangesNothingAndWritesNoAuditRow()
    {
        await AddTenantAsync("Fontanería A", 1);
        var savesBefore = _db.SaveCount;

        await Handler().Handle(new ListTenantsQuery(), CancellationToken.None);

        // R17: las lecturas quedan en el log, no en la tabla de auditoría. Y no guardan nada.
        Assert.Equal(savesBefore, _db.SaveCount);
    }

    [Fact]
    public async Task WithoutTheOperatorIdentity_NothingIsListed()
    {
        await AddTenantAsync("Fontanería A", 1);
        var handler = new ListTenantsQueryHandler(
            _db, new FakeCurrentUser(role: null, userId: null, platformSession: true, platformOperatorActive: true), _logger);

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ListTenantsQuery(), CancellationToken.None));

        Assert.Equal(TenancyErrors.Forbidden, denied.Code);
        Assert.Empty(_logger.Entries);
    }
}

public class ListTenantsQueryValidatorTests
{
    private static readonly ListTenantsQueryValidator Validator = new();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 20)]
    [InlineData(1, 100)]
    [InlineData(10_000_000, 100)]
    public void ValidPaging_IsAccepted(int page, int pageSize) =>
        Assert.True(Validator.Validate(new ListTenantsQuery(page, pageSize)).IsValid);

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(int.MinValue, 20)]
    [InlineData(10_000_001, 20)]
    [InlineData(int.MaxValue, 100)]
    public void AnInvalidPage_IsRejected(int page, int pageSize)
    {
        var result = Validator.Validate(new ListTenantsQuery(page, pageSize));

        Assert.Equal(nameof(ListTenantsQuery.Page), Assert.Single(result.Errors).PropertyName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void APageSizeOutOfRange_IsRejected_NotSilentlyTruncated(int pageSize)
    {
        // El tope de 100 (R11) es un error de validación, no una página recortada sin avisar.
        var result = Validator.Validate(new ListTenantsQuery(1, pageSize));

        Assert.Equal(nameof(ListTenantsQuery.PageSize), Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void ThePageSizeCap_IsOneHundred() => Assert.Equal(100, ListTenantsQuery.MaxPageSize);
}

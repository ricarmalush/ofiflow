using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Platform;
using OfiFlow.Application.Platform.Commands.ReactivateTenant;
using OfiFlow.Application.Platform.Commands.SuspendTenant;
using OfiFlow.Application.Platform.Queries.ListTenants;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Listar, suspender y reactivar (spec 011, bloque 6) contra SQL Server real (ADR-008): el recuento de miembros cruza el filtro de
/// empresa (lo que un doble no puede probar), suspender es <b>todo o nada</b> con su fila de auditoría de verdad (una sentencia que
/// falla deshace la otra) y dos suspensiones simultáneas de la misma empresa dejan exactamente una fila.
/// </summary>
public class PlatformTenantPanelPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;
    private readonly Guid _operatorId = Guid.NewGuid();

    /// <summary>Un interceptor que registra las sentencias y hace fallar la primera que contenga un texto: una base de datos que rechaza una sentencia.</summary>
    private sealed class StatementFailer(string? failOn = null) : DbCommandInterceptor
    {
        public List<string> Executed { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (failOn is not null && command.CommandText.Contains(failOn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"La base de datos rechazó la sentencia que contiene «{failOn}».");
            }

            Executed.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // Un operador no tiene empresa: el contexto se abre con una cualquiera. Las tablas de empresas y de auditoría no se filtran.
    private ApplicationDbContext Open(Action<DbContextOptionsBuilder<ApplicationDbContext>>? configure = null) =>
        _fixture.CreateDbContext(Guid.NewGuid(), configure ?? (_ => { }));

    private static TestCurrentUser Operator(Guid id) => new(role: null, userId: id, tenantId: null);

    private async Task<Tenant> SeedTenantAsync(string name, int members, bool suspended = false)
    {
        var tenant = Tenant.Create(name);
        if (suspended)
        {
            tenant.Suspend(Now.AddDays(-2));
        }

        await using var db = _fixture.CreateDbContext(tenant.Id);
        db.Tenants.Add(tenant);
        for (var index = 0; index < members; index++)
        {
            db.TenantUsers.Add(TenantUser.Create(tenant.Id, Guid.NewGuid(), index == 0 ? TenantRole.Owner : TenantRole.Technician));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return tenant;
    }

    private async Task<DateTime?> SuspendedAtAsync(Guid tenantId)
    {
        await using var db = Open();
        return (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).SuspendedAt;
    }

    private async Task<int> AuditRowsAsync(Guid tenantId, string action)
    {
        await using var db = Open();
        return await db.PlatformAuditEntries.AsNoTracking().CountAsync(e => e.TenantId == tenantId && e.Action == action);
    }

    private SuspendTenantCommandHandler Suspender(ApplicationDbContext db) =>
        new(db, Operator(_operatorId), new PlatformAudit(db), new FakeTimeProvider(Now), new ListLogger<SuspendTenantCommandHandler>());

    private ReactivateTenantCommandHandler Reactivator(ApplicationDbContext db) =>
        new(db, Operator(_operatorId), new PlatformAudit(db), new FakeTimeProvider(Now), new ListLogger<ReactivateTenantCommandHandler>());

    // ===== Listar =====

    [Fact]
    public async Task Listing_CountsTheMembersOfEveryCompany_EvenThoughTheOperatorHasNoActiveCompany()
    {
        var a = await SeedTenantAsync("Fontanería A", members: 3);
        var b = await SeedTenantAsync("Electricidad B", members: 1);
        var empty = await SeedTenantAsync("Sin miembros", members: 0);

        await using var db = Open();
        var handler = new ListTenantsQueryHandler(db, Operator(_operatorId), new ListLogger<ListTenantsQueryHandler>());
        var result = await handler.Handle(new ListTenantsQuery(PageSize: 100), CancellationToken.None);

        // El contexto está abierto para OTRA empresa: sin saltarse el filtro, todos los recuentos serían cero.
        Assert.Equal(3, result.Items.Single(t => t.Id == a.Id).MemberCount);
        Assert.Equal(1, result.Items.Single(t => t.Id == b.Id).MemberCount);
        Assert.Equal(0, result.Items.Single(t => t.Id == empty.Id).MemberCount);
    }

    [Fact]
    public async Task Listing_ReportsTheSuspensionAndFiltersByIt()
    {
        var active = await SeedTenantAsync("Activa", members: 1);
        var suspended = await SeedTenantAsync("Suspendida", members: 1, suspended: true);

        await using var db = Open();
        var handler = new ListTenantsQueryHandler(db, Operator(_operatorId), new ListLogger<ListTenantsQueryHandler>());
        var onlySuspended = await handler.Handle(new ListTenantsQuery(PageSize: 100, Suspended: true), CancellationToken.None);
        var onlyActive = await handler.Handle(new ListTenantsQuery(PageSize: 100, Suspended: false), CancellationToken.None);

        Assert.Contains(onlySuspended.Items, t => t.Id == suspended.Id && t.SuspendedAt == Now.AddDays(-2));
        Assert.DoesNotContain(onlySuspended.Items, t => t.Id == active.Id);
        Assert.Contains(onlyActive.Items, t => t.Id == active.Id && t.SuspendedAt is null);
        Assert.DoesNotContain(onlyActive.Items, t => t.Id == suspended.Id);
    }

    [Fact]
    public async Task Listing_PagesWithATotalOrder_AgainstTheRealDatabase()
    {
        for (var index = 0; index < 7; index++)
        {
            await SeedTenantAsync($"Para paginar {index}", members: 0);
        }

        await using var db = Open();
        var handler = new ListTenantsQueryHandler(db, Operator(_operatorId), new ListLogger<ListTenantsQueryHandler>());
        var first = await handler.Handle(new ListTenantsQuery(Page: 1, PageSize: 3), CancellationToken.None);
        var seen = new List<Guid>();
        for (var page = 1; page <= (first.TotalCount + 2) / 3; page++)
        {
            seen.AddRange((await handler.Handle(new ListTenantsQuery(Page: page, PageSize: 3), CancellationToken.None)).Items.Select(t => t.Id));
        }

        Assert.Equal(first.TotalCount, seen.Count);
        Assert.Equal(first.TotalCount, seen.Distinct().Count());
    }

    [Fact]
    public async Task Listing_RunsAsOneCountPlusOnePageQuery_NotOnePerCompany()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedTenantAsync($"Para contar consultas {index}", members: 2);
        }

        var queries = new StatementFailer();
        await using var db = Open(options => options.AddInterceptors(queries));
        await new ListTenantsQueryHandler(db, Operator(_operatorId), new ListLogger<ListTenantsQueryHandler>())
            .Handle(new ListTenantsQuery(PageSize: 100), CancellationToken.None);

        // El recuento de miembros va dentro de la misma consulta de la página: no hay un viaje a la base de datos por empresa.
        Assert.Equal(2, queries.Executed.Count);
    }

    [Fact]
    public async Task TheListQuery_OrdersByCreationDescendingAndThenById_InTheSqlItSends()
    {
        // El desempate por Id es lo que hace TOTAL el orden (R11). SQL Server devuelve los empates en el orden de su índice, que casi
        // siempre coincide con el Id, así que quitarlo no cambiaría los resultados de un test: se fija en la propia sentencia.
        var queries = new StatementFailer();
        await using var db = Open(options => options.AddInterceptors(queries));
        await new ListTenantsQueryHandler(db, Operator(_operatorId), new ListLogger<ListTenantsQueryHandler>())
            .Handle(new ListTenantsQuery(), CancellationToken.None);

        var pageQuery = queries.Executed.Single(sql => sql.Contains("OFFSET", StringComparison.Ordinal));
        Assert.Matches(@"ORDER BY \[\w+\]\.\[CreatedAt\] DESC, \[\w+\]\.\[Id\]", pageQuery);
    }

    // ===== Suspender y reactivar =====

    [Fact]
    public async Task Suspending_PersistsTheStateAndExactlyOneAuditRow_AndTouchesNoData()
    {
        var tenant = await SeedTenantAsync("Fontanería A", members: 3);

        await using (var db = Open())
        {
            await Suspender(db).Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None);
        }

        Assert.Equal(Now, await SuspendedAtAsync(tenant.Id));
        Assert.Equal(1, await AuditRowsAsync(tenant.Id, PlatformAuditActions.TenantSuspended));

        // R12: ningún dato de la empresa cambia.
        await using var read = _fixture.CreateDbContext(tenant.Id);
        Assert.Equal(3, await read.TenantUsers.CountAsync());
        Assert.Equal("Fontanería A", (await read.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id)).Name);

        // La fila lleva solo Ids, el código de acción y la hora.
        await using var audit = Open();
        var row = await audit.PlatformAuditEntries.AsNoTracking().SingleAsync(e => e.TenantId == tenant.Id);
        Assert.Equal((_operatorId, PlatformAuditActions.TenantSuspended, Now), (row.OperatorUserId, row.Action, row.OccurredAt));
    }

    [Fact]
    public async Task Reactivating_ClearsTheStateAndWritesItsOwnAuditRow()
    {
        var tenant = await SeedTenantAsync("Suspendida", members: 1, suspended: true);

        await using (var db = Open())
        {
            await Reactivator(db).Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None);
        }

        Assert.Null(await SuspendedAtAsync(tenant.Id));
        Assert.Equal(1, await AuditRowsAsync(tenant.Id, PlatformAuditActions.TenantReactivated));
    }

    [Fact]
    public async Task WhenTheAuditRowCannotBeWritten_TheTenantIsNotSuspended()
    {
        // «Todo o nada» de verdad (R17): la base de datos rechaza la sentencia de la auditoría y la suspensión, que va en la misma
        // transacción, se deshace. Si la suspensión y la fila se confirmaran por separado, la empresa quedaría suspendida sin rastro.
        var tenant = await SeedTenantAsync("Fontanería A", members: 1);
        var failer = new StatementFailer(failOn: "PlatformAuditEntries");

        await using (var db = Open(options => options.AddInterceptors(failer)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Suspender(db).Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None));
        }

        Assert.Null(await SuspendedAtAsync(tenant.Id));
        Assert.Equal(0, await AuditRowsAsync(tenant.Id, PlatformAuditActions.TenantSuspended));

        // Control positivo: sin el fallo, la misma suspensión sí se confirma. Y las sentencias de la transacción fueron las dos.
        var healthy = new StatementFailer();
        await using (var db = Open(options => options.AddInterceptors(healthy)))
        {
            await Suspender(db).Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None);
        }

        Assert.Equal(Now, await SuspendedAtAsync(tenant.Id));
        Assert.Contains(healthy.Executed, sql => sql.Contains("UPDATE [Tenants]", StringComparison.Ordinal));
        Assert.Contains(healthy.Executed, sql => sql.Contains("INSERT INTO [PlatformAuditEntries]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheTenantUpdateIsTheOneThatFails_NoAuditRowIsLeftBehind()
    {
        // El otro sentido: la fila de auditoría ya se escribió dentro de la transacción y falla la actualización de la empresa. Tampoco
        // queda «rastro de algo que no ocurrió».
        var tenant = await SeedTenantAsync("Fontanería A", members: 1);
        var failer = new StatementFailer(failOn: "UPDATE [Tenants]");

        await using (var db = Open(options => options.AddInterceptors(failer)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Suspender(db).Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None));
        }

        Assert.Null(await SuspendedAtAsync(tenant.Id));
        Assert.Equal(0, await AuditRowsAsync(tenant.Id, PlatformAuditActions.TenantSuspended));
    }

    [Fact]
    public async Task TwoRequestsHoldingTheSameTenant_OnlyOneCanCommit_SoThereIsOneAuditRow()
    {
        // Determinista: las dos peticiones leen la empresa activa ANTES de que ninguna confirme. La versión de fila hace que solo
        // la primera en confirmar gane; sin ella, las dos confirmarían y quedarían dos filas de auditoría para una suspensión.
        var tenant = await SeedTenantAsync("Fontanería A", members: 1);

        await using var first = Open();
        await using var second = Open();
        var firstTenant = await first.Tenants.SingleAsync(t => t.Id == tenant.Id);
        var secondTenant = await second.Tenants.SingleAsync(t => t.Id == tenant.Id);

        firstTenant.Suspend(Now);
        new PlatformAudit(first).Add(_operatorId, PlatformAuditActions.TenantSuspended, tenant.Id, Now);
        await first.SaveChangesAsync(CancellationToken.None);

        secondTenant.Suspend(Now.AddSeconds(1));
        new PlatformAudit(second).Add(_operatorId, PlatformAuditActions.TenantSuspended, tenant.Id, Now.AddSeconds(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(CancellationToken.None));

        Assert.Equal(Now, await SuspendedAtAsync(tenant.Id));
        Assert.Equal(1, await AuditRowsAsync(tenant.Id, PlatformAuditActions.TenantSuspended));
    }

    [Fact]
    public async Task ManySimultaneousSuspensions_LeaveExactlyOneAuditRow_AndEveryLoserSeesTheBusinessError()
    {
        var tenant = await SeedTenantAsync("Fontanería A", members: 1);

        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Open();
            try
            {
                await Suspender(db).Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None);
                return (string?)null;
            }
            catch (DomainException error)
            {
                return error.Code;
            }
        });

        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(code => code is null));
        Assert.All(outcomes.Where(code => code is not null), code => Assert.Equal(TenancyErrors.TenantAlreadySuspended, code));
        Assert.Equal(1, await AuditRowsAsync(tenant.Id, PlatformAuditActions.TenantSuspended));
        Assert.Equal(Now, await SuspendedAtAsync(tenant.Id));
    }

    [Fact]
    public async Task SuspendingOneCompany_DoesNotTouchAnotherOne()
    {
        var target = await SeedTenantAsync("Objetivo", members: 1);
        var other = await SeedTenantAsync("Otra", members: 1);

        await using (var db = Open())
        {
            await Suspender(db).Handle(new SuspendTenantCommand(target.Id), CancellationToken.None);
        }

        Assert.Null(await SuspendedAtAsync(other.Id));
        Assert.Equal(0, await AuditRowsAsync(other.Id, PlatformAuditActions.TenantSuspended));
    }

    [Fact]
    public async Task ATenantThatDoesNotExist_IsNotFound_AndLeavesNoAuditRow()
    {
        var missing = Guid.NewGuid();

        await using (var db = Open())
        {
            await Assert.ThrowsAsync<NotFoundException>(() => Suspender(db).Handle(new SuspendTenantCommand(missing), CancellationToken.None));
            await Assert.ThrowsAsync<NotFoundException>(() => Reactivator(db).Handle(new ReactivateTenantCommand(missing), CancellationToken.None));
        }

        Assert.Equal(0, await AuditRowsAsync(missing, PlatformAuditActions.TenantSuspended));
        Assert.Equal(0, await AuditRowsAsync(missing, PlatformAuditActions.TenantReactivated));
    }
}

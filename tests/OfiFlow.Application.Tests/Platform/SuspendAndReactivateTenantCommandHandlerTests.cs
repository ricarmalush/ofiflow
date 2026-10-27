using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Platform;
using OfiFlow.Application.Platform.Commands.ReactivateTenant;
using OfiFlow.Application.Platform.Commands.SuspendTenant;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Platform;

/// <summary>
/// Suspender y reactivar una empresa (spec 011 R12, R13 y R17), con dobles: el cambio y su fila de auditoría se confirman en un único
/// SaveChanges, un error de negocio no deja ninguna fila y el rastro (evento y fila) lleva solo Ids. Que sea todo o nada contra una
/// base de datos real, y con dos peticiones a la vez, lo prueban <c>SuspendTenantPersistenceTests</c>.
/// </summary>
public class SuspendAndReactivateTenantCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakePlatformAudit _audit = new();
    private readonly ListLogger<SuspendTenantCommandHandler> _suspendLog = new();
    private readonly ListLogger<ReactivateTenantCommandHandler> _reactivateLog = new();
    private readonly Guid _operatorId = Guid.NewGuid();

    private FakeCurrentUser Operator(Guid? userId) =>
        new(role: null, userId: userId, platformSession: true, platformOperatorActive: true);

    private SuspendTenantCommandHandler Suspender(Guid? userId = null, bool anonymous = false) =>
        new(_db, Operator(anonymous ? null : userId ?? _operatorId), _audit, new FakeTimeProvider(Now), _suspendLog);

    private ReactivateTenantCommandHandler Reactivator(Guid? userId = null, bool anonymous = false) =>
        new(_db, Operator(anonymous ? null : userId ?? _operatorId), _audit, new FakeTimeProvider(Now), _reactivateLog);

    private async Task<Tenant> AddTenantAsync(bool suspended = false)
    {
        var tenant = Tenant.Create("Fontanería A");
        if (suspended)
        {
            tenant.Suspend(Now.AddDays(-3));
        }

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
        _db.ResetSaveCount();

        return tenant;
    }

    private async Task<Tenant> ReloadAsync(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.Tenants.SingleAsync(t => t.Id == id);
    }

    // ===== Suspender =====

    [Fact]
    public async Task Suspending_AnActiveTenant_SetsTheServerTime_WritesOneAuditRow_AndCommitsOnce()
    {
        var tenant = await AddTenantAsync();

        await Suspender().Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None);

        Assert.Equal(Now, (await ReloadAsync(tenant.Id)).SuspendedAt);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((_operatorId, PlatformAuditActions.TenantSuspended, (Guid?)tenant.Id, Now), (entry.OperatorUserId, entry.Action, entry.TenantId, entry.At));
        Assert.Equal(1, _db.SaveCount);
    }

    [Fact]
    public async Task Suspending_LeavesTheEvent1604_WithOnlyTheTwoIds()
    {
        var tenant = await AddTenantAsync();

        await Suspender().Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None);

        var logged = Assert.Single(_suspendLog.Entries);
        Assert.Equal(SecurityEventIds.PlatformTenantSuspended, logged.EventId.Id);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Contains(_operatorId.ToString(), logged.Message);
        Assert.Contains(tenant.Id.ToString(), logged.Message);
        Assert.DoesNotContain("Fontanería", logged.Message);
    }

    [Fact]
    public async Task Suspending_ASuspendedTenant_IsABusinessError_AndWritesNothing()
    {
        var tenant = await AddTenantAsync(suspended: true);

        var error = await Assert.ThrowsAsync<DomainException>(() => Suspender().Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantAlreadySuspended, error.Code);
        Assert.Empty(_audit.Entries); // no se escribe otra fila (criterio de aceptación)
        Assert.Equal(0, _db.SaveCount);
        Assert.Empty(_suspendLog.Entries);
        Assert.Equal(Now.AddDays(-3), (await ReloadAsync(tenant.Id)).SuspendedAt); // la hora original no se pisa
    }

    [Fact]
    public async Task Suspending_ATenantThatDoesNotExist_IsNotFound_AndWritesNothing()
    {
        var missing = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Suspender().Handle(new SuspendTenantCommand(missing), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantNotFound, error.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
    }

    [Fact]
    public async Task Suspending_WhenTheCommitFails_LeavesNoAuditRowCommitted_AndTheTenantStaysActive()
    {
        // R17 y criterio de aceptación: «si la acción falla a mitad, la empresa no queda suspendida». Aquí el SaveChanges lanza: como el
        // cambio y la fila se confirman juntos, ninguno de los dos queda. (El doble no puede probar la transacción real: lo hace SQL Server.)
        var tenant = await AddTenantAsync();
        _db.ExceptionOnSave = new DbUpdateException("la base de datos no responde");

        await Assert.ThrowsAsync<DbUpdateException>(() => Suspender().Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Empty(_suspendLog.Entries); // y el evento no se deja: la acción no ocurrió
        _db.ExceptionOnSave = null;
        Assert.Null((await ReloadAsync(tenant.Id)).SuspendedAt);
    }

    [Fact]
    public async Task Suspending_WhenAnotherRequestWonTheRace_IsTheSameBusinessErrorAsArrivingLater()
    {
        var tenant = await AddTenantAsync();
        _db.ExceptionOnSave = new DbUpdateConcurrencyException("otra petición suspendió la empresa a la vez");

        var error = await Assert.ThrowsAsync<DomainException>(() => Suspender().Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantAlreadySuspended, error.Code);
        Assert.Empty(_suspendLog.Entries);
    }

    [Fact]
    public async Task Suspending_WithoutTheOperatorIdentity_IsDenied_BeforeTouchingAnything()
    {
        var tenant = await AddTenantAsync();

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => Suspender(anonymous: true).Handle(new SuspendTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Equal(TenancyErrors.Forbidden, denied.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
    }

    [Fact]
    public async Task Suspending_OneTenant_DoesNotTouchAnother()
    {
        var target = await AddTenantAsync();
        var other = await AddTenantAsync();

        await Suspender().Handle(new SuspendTenantCommand(target.Id), CancellationToken.None);

        Assert.Null((await ReloadAsync(other.Id)).SuspendedAt);
        Assert.Equal(target.Id, Assert.Single(_audit.Entries).TenantId);
    }

    // ===== Reactivar =====

    [Fact]
    public async Task Reactivating_ASuspendedTenant_ClearsTheState_WritesOneAuditRow_AndCommitsOnce()
    {
        var tenant = await AddTenantAsync(suspended: true);

        await Reactivator().Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None);

        Assert.Null((await ReloadAsync(tenant.Id)).SuspendedAt);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((_operatorId, PlatformAuditActions.TenantReactivated, (Guid?)tenant.Id, Now), (entry.OperatorUserId, entry.Action, entry.TenantId, entry.At));
        Assert.Equal(1, _db.SaveCount);

        var logged = Assert.Single(_reactivateLog.Entries);
        Assert.Equal(SecurityEventIds.PlatformTenantReactivated, logged.EventId.Id);
        Assert.Contains(tenant.Id.ToString(), logged.Message);
    }

    [Fact]
    public async Task Reactivating_AnActiveTenant_IsABusinessError_AndWritesNothing()
    {
        var tenant = await AddTenantAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => Reactivator().Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantNotSuspended, error.Code);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
        Assert.Empty(_reactivateLog.Entries);
    }

    [Fact]
    public async Task Reactivating_ATenantThatDoesNotExist_IsTheSameNotFoundAsSuspending()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => Reactivator().Handle(new ReactivateTenantCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantNotFound, error.Code);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task Reactivating_WhenTheCommitFails_LeavesTheTenantSuspended_AndNoEvent()
    {
        var tenant = await AddTenantAsync(suspended: true);
        _db.ExceptionOnSave = new DbUpdateException("la base de datos no responde");

        await Assert.ThrowsAsync<DbUpdateException>(() => Reactivator().Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Empty(_reactivateLog.Entries);
        _db.ExceptionOnSave = null;
        Assert.NotNull((await ReloadAsync(tenant.Id)).SuspendedAt);
    }

    [Fact]
    public async Task Reactivating_WhenAnotherRequestWonTheRace_IsTheSameBusinessErrorAsArrivingLater()
    {
        var tenant = await AddTenantAsync(suspended: true);
        _db.ExceptionOnSave = new DbUpdateConcurrencyException("otra petición la reactivó a la vez");

        var error = await Assert.ThrowsAsync<DomainException>(() => Reactivator().Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Equal(TenancyErrors.TenantNotSuspended, error.Code);
    }

    [Fact]
    public async Task Reactivating_WithoutTheOperatorIdentity_IsDenied()
    {
        var tenant = await AddTenantAsync(suspended: true);

        await Assert.ThrowsAsync<ForbiddenException>(() => Reactivator(anonymous: true).Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None));

        Assert.Empty(_audit.Entries);
    }

    // ===== La auditoría solo lleva Ids =====

    [Fact]
    public void TheAuditActionCodes_AreStableAndDistinct()
    {
        Assert.Equal("platform.tenant_suspended", PlatformAuditActions.TenantSuspended);
        Assert.Equal("platform.tenant_reactivated", PlatformAuditActions.TenantReactivated);
        Assert.NotEqual(PlatformAuditActions.TenantSuspended, PlatformAuditActions.TenantReactivated);
    }
}

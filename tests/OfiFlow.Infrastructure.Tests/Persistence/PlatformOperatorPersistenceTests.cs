using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Platform;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// La identidad del operador de la plataforma (spec 011, bloque 3) contra SQL Server real (ADR-008): la regla R1 (una cuenta
/// es de empresa o de plataforma, nunca de las dos), la clave única, la baja y la auditoría de solo inserción.
/// </summary>
public class PlatformOperatorPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    // Estas tablas no son de una empresa: el contexto se abre con una cualquiera y no las filtra.
    private ApplicationDbContext Open(Guid? tenantId = null) => _fixture.CreateDbContext(tenantId ?? Guid.NewGuid());

    private async Task<PlatformOperatorCreation> AddOperatorAsync(Guid userId)
    {
        await using var db = Open();
        var result = await new PlatformOperators(db).AddAsync(userId, Now, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        return result;
    }

    private async Task SeedMembershipAsync(Guid userId, Guid tenantId)
    {
        await using var db = Open(tenantId);
        db.TenantUsers.Add(TenantUser.Create(tenantId, userId, TenantRole.Technician));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // ===== Alta, lectura y baja =====

    [Fact]
    public async Task AnOperator_SurvivesARoundTrip_AndIsActive()
    {
        var userId = Guid.NewGuid();

        Assert.Equal(PlatformOperatorCreation.Added, await AddOperatorAsync(userId));

        await using var read = Open();
        var loaded = await read.PlatformOperators.AsNoTracking().SingleAsync(o => o.UserId == userId);
        Assert.Equal(Now, loaded.CreatedAt);
        Assert.Null(loaded.DisabledAt);
        Assert.True(loaded.IsActive);
        Assert.True(await new PlatformOperators(read).IsActiveOperatorAsync(userId, CancellationToken.None));
    }

    [Fact]
    public async Task AnAccountThatIsNotAnOperator_IsNotActive()
    {
        await using var db = Open();

        Assert.False(await new PlatformOperators(db).IsActiveOperatorAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Disabling_StopsTheOperatorBeingActive_ButKeepsTheRow()
    {
        var userId = Guid.NewGuid();
        await AddOperatorAsync(userId);

        await using (var db = Open())
        {
            Assert.True(await new PlatformOperators(db).DisableAsync(userId, Now.AddHours(1), CancellationToken.None));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        Assert.False(await new PlatformOperators(read).IsActiveOperatorAsync(userId, CancellationToken.None));
        var row = await read.PlatformOperators.AsNoTracking().SingleAsync(o => o.UserId == userId); // la fila no se borra
        Assert.Equal(Now.AddHours(1), row.DisabledAt);
        Assert.Equal(Now, row.CreatedAt);
    }

    [Fact]
    public async Task Disabling_AnUnknownAccount_OrAnAlreadyDisabledOne_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await AddOperatorAsync(userId);

        await using var db = Open();
        var operators = new PlatformOperators(db);
        Assert.True(await operators.DisableAsync(userId, Now, CancellationToken.None)); // control: la primera vez sí
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.False(await operators.DisableAsync(userId, Now.AddHours(1), CancellationToken.None));
        Assert.False(await operators.DisableAsync(Guid.NewGuid(), Now, CancellationToken.None));
        await db.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(Now, (await db.PlatformOperators.AsNoTracking().SingleAsync(o => o.UserId == userId)).DisabledAt); // no se pisó
    }

    // ===== R1: una cuenta es de empresa o de plataforma, nunca de las dos =====

    [Fact]
    public async Task AnAccountThatBelongsToACompany_CannotBecomeAnOperator()
    {
        var userId = Guid.NewGuid();
        await SeedMembershipAsync(userId, Guid.NewGuid());

        var result = await AddOperatorAsync(userId);

        Assert.Equal(PlatformOperatorCreation.AccountBelongsToACompany, result);
        await using var read = Open();
        Assert.False(await read.PlatformOperators.AnyAsync(o => o.UserId == userId)); // no se añadió nada
    }

    [Fact]
    public async Task TheMembershipCheck_SeesEveryCompany_NotJustTheOneTheContextWasOpenedFor()
    {
        // Sin empresa activa (el operador no tiene ninguna), la comprobación debe mirar TODAS: si el filtro de empresa la
        // limitara a la del contexto, una cuenta de otra empresa pasaría por operador. Es el motivo del IgnoreQueryFilters.
        var userId = Guid.NewGuid();
        var companyOfTheAccount = Guid.NewGuid();
        await SeedMembershipAsync(userId, companyOfTheAccount);

        await using var db = Open(tenantId: Guid.NewGuid()); // el contexto es de OTRA empresa
        var result = await new PlatformOperators(db).AddAsync(userId, Now, CancellationToken.None);

        Assert.Equal(PlatformOperatorCreation.AccountBelongsToACompany, result);
    }

    [Fact]
    public async Task AnAccountWithoutAnyMembership_CanBecomeAnOperator()
    {
        // Control positivo: la comprobación no deniega siempre. Una membresía de OTRA cuenta no cuenta.
        await SeedMembershipAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(PlatformOperatorCreation.Added, await AddOperatorAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AnOperator_CannotBeAddedTwice_EvenAfterBeingDisabled()
    {
        var userId = Guid.NewGuid();
        await AddOperatorAsync(userId);

        Assert.Equal(PlatformOperatorCreation.AlreadyAnOperator, await AddOperatorAsync(userId));

        await using (var db = Open())
        {
            await new PlatformOperators(db).DisableAsync(userId, Now, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Dada de baja, no se «reactiva» por la puerta de atrás con un alta: la fila sigue ahí (D11: se da de baja y se crea otra cuenta).
        Assert.Equal(PlatformOperatorCreation.AlreadyAnOperator, await AddOperatorAsync(userId));
    }

    [Fact]
    public async Task TheOperatorKey_IsUniquePerAccount_InTheDatabase()
    {
        var userId = Guid.NewGuid();
        await using (var db = Open())
        {
            db.PlatformOperators.Add(PlatformOperator.Create(userId, Now));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var second = Open();
        second.PlatformOperators.Add(PlatformOperator.Create(userId, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    // ===== La auditoría: solo inserción =====

    [Fact]
    public async Task AnAuditEntry_SurvivesARoundTrip_WithOnlyIdsACodeAndTheTime()
    {
        var operatorId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await using (var db = Open())
        {
            new PlatformAudit(db).Add(operatorId, PlatformAuditActions.TenantSuspended, tenantId, Now);
            new PlatformAudit(db).Add(operatorId, PlatformAuditActions.SessionIssued, tenantId: null, Now.AddMinutes(1));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        var entries = await read.PlatformAuditEntries.AsNoTracking().Where(e => e.OperatorUserId == operatorId).OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal([PlatformAuditActions.TenantSuspended, PlatformAuditActions.SessionIssued], entries.Select(e => e.Action));
        Assert.Equal([tenantId, null], entries.Select(e => e.TenantId));
        Assert.Equal([Now, Now.AddMinutes(1)], entries.Select(e => e.OccurredAt));
    }

    [Fact]
    public async Task AnAuditEntry_CannotBeModified()
    {
        var operatorId = Guid.NewGuid();
        await using (var db = Open())
        {
            new PlatformAudit(db).Add(operatorId, PlatformAuditActions.OperatorCreated, tenantId: null, Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var tamper = Open();
        var entry = await tamper.PlatformAuditEntries.SingleAsync(e => e.OperatorUserId == operatorId);
        tamper.Entry(entry).Property(e => e.Action).CurrentValue = PlatformAuditActions.OperatorDisabled; // intento de reescribir la historia

        await Assert.ThrowsAsync<InvalidOperationException>(() => tamper.SaveChangesAsync(CancellationToken.None));

        await using var read = Open();
        Assert.Equal(PlatformAuditActions.OperatorCreated, (await read.PlatformAuditEntries.AsNoTracking().SingleAsync(e => e.OperatorUserId == operatorId)).Action);
    }

    [Fact]
    public async Task AnAuditEntry_CannotBeDeleted()
    {
        var operatorId = Guid.NewGuid();
        await using (var db = Open())
        {
            new PlatformAudit(db).Add(operatorId, PlatformAuditActions.OperatorCreated, tenantId: null, Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var tamper = Open();
        tamper.PlatformAuditEntries.Remove(await tamper.PlatformAuditEntries.SingleAsync(e => e.OperatorUserId == operatorId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => tamper.SaveChangesAsync(CancellationToken.None));

        await using var read = Open();
        Assert.Equal(1, await read.PlatformAuditEntries.CountAsync(e => e.OperatorUserId == operatorId));
    }

    [Fact]
    public async Task TheAuditGuard_DoesNotStopOtherEntitiesFromBeingModifiedOrDeleted()
    {
        // Control positivo: el bloqueo es solo para la auditoría. Dar de baja a un operador (una modificación) sigue funcionando
        // aunque en el mismo contexto se inserten filas de auditoría.
        var userId = Guid.NewGuid();
        await AddOperatorAsync(userId);

        await using var db = Open();
        await new PlatformOperators(db).DisableAsync(userId, Now, CancellationToken.None);
        new PlatformAudit(db).Add(userId, PlatformAuditActions.OperatorDisabled, tenantId: null, Now);

        Assert.Equal(2, await db.SaveChangesAsync(CancellationToken.None)); // la baja y la fila de auditoría
    }

    [Fact]
    public async Task TheAuditEntry_AndTheAction_CommitTogetherOrNotAtAll()
    {
        // R17: la fila de auditoría entra en la MISMA transacción que la acción. Si la acción falla, no queda fila.
        var userId = Guid.NewGuid();
        await AddOperatorAsync(userId);
        var marker = Guid.NewGuid(); // identifica la fila de auditoría de este test

        await using (var db = Open())
        {
            db.PlatformOperators.Add(PlatformOperator.Create(userId, Now)); // choca con la clave única: la «acción» falla
            new PlatformAudit(db).Add(marker, PlatformAuditActions.OperatorCreated, tenantId: null, Now);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));
        }

        await using var read = Open();
        Assert.False(await read.PlatformAuditEntries.AnyAsync(e => e.OperatorUserId == marker)); // y la auditoría tampoco quedó
    }

    [Fact]
    public async Task AnAuditActionLongerThanTheLimit_IsRejectedBeforeReachingTheDatabase()
    {
        await using var db = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlatformAudit(db).Add(Guid.NewGuid(), new string('a', PlatformAuditActions.MaxLength + 1), tenantId: null, Now));
    }

    [Fact]
    public void EveryAuditActionCode_IsStable_Distinct_AndFitsTheColumn()
    {
        var codes = typeof(PlatformAuditActions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(5, codes.Count); // si se añade una acción, hay que añadirla aquí a propósito
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code =>
        {
            Assert.StartsWith("platform.", code, StringComparison.Ordinal);
            Assert.True(code.Length <= PlatformAuditActions.MaxLength);
            Assert.Equal(code.ToLowerInvariant(), code);
        });
    }
}

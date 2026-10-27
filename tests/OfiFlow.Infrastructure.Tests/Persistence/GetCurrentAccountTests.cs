using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Identity.Queries.GetCurrentAccount;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// «Quién soy» (spec 012) contra SQL Server real (ADR-008): la consulta cruza tablas con filtro de empresa (pertenencias) y sin él
/// (personas y empresas), y esa mezcla es justo lo que un doble en memoria no puede probar. Dos empresas, y lo que una no debe ver
/// de la otra.
/// </summary>
public class GetCurrentAccountTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    private sealed record Person(Guid UserId, Guid TenantId, Guid MembershipId);

    private async Task<Person> SeedAsync(Guid tenantId, string name, string email, TenantRole role)
    {
        var user = User.Create(Guid.NewGuid(), name, Email.Create(email));
        var membership = TenantUser.Create(tenantId, user.Id, role);

        await using var db = _fixture.CreateDbContext(tenantId);
        db.Users.Add(user);
        db.TenantUsers.Add(membership);
        await db.SaveChangesAsync(CancellationToken.None);

        return new Person(user.Id, tenantId, membership.Id);
    }

    private async Task<Guid> SeedTenantAsync(string name)
    {
        var tenant = Tenant.Create(name);
        await using var db = _fixture.CreateDbContext(tenant.Id);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(CancellationToken.None);

        return tenant.Id;
    }

    /// <summary>La petición de <paramref name="who"/>, con el contexto abierto para <paramref name="activeTenant"/> (la empresa del token).</summary>
    private async Task<OfiFlow.Application.Identity.CurrentAccountDto> AskAsync(Person who, Guid activeTenant, TenantRole role)
    {
        await using var db = _fixture.CreateDbContext(activeTenant);
        var handler = new GetCurrentAccountQueryHandler(db, new TestCurrentUser(role, who.UserId, who.TenantId));

        return await handler.Handle(new GetCurrentAccountQuery(), CancellationToken.None);
    }

    [Fact]
    public async Task ReturnsThePersonTheirCompanyAndTheMembership_FromTheRealDatabase()
    {
        var tenant = await SeedTenantAsync("Fontanería Real");
        var ana = await SeedAsync(tenant, "Ana Pérez", "ana@real.test", TenantRole.Manager);

        var account = await AskAsync(ana, tenant, TenantRole.Manager);

        Assert.Equal(("Ana Pérez", "ana@real.test", "Fontanería Real", "Manager"), (account.Name, account.Email, account.TenantName, account.Role));
        Assert.Equal((ana.UserId, ana.MembershipId, tenant), (account.UserId, account.MembershipId, account.TenantId));
    }

    [Fact]
    public async Task InACompanyWithSeveralMembers_EachOneGetsTheirOwn()
    {
        var tenant = await SeedTenantAsync("Empresa con equipo");
        var ana = await SeedAsync(tenant, "Ana", "ana@equipo.test", TenantRole.Owner);
        var bruno = await SeedAsync(tenant, "Bruno", "bruno@equipo.test", TenantRole.Technician);

        var asAna = await AskAsync(ana, tenant, TenantRole.Owner);
        var asBruno = await AskAsync(bruno, tenant, TenantRole.Technician);

        Assert.Equal(("Ana", ana.MembershipId), (asAna.Name, asAna.MembershipId));
        Assert.Equal(("Bruno", bruno.MembershipId), (asBruno.Name, asBruno.MembershipId));
        Assert.NotEqual(asAna.MembershipId, asBruno.MembershipId);
    }

    [Fact]
    public async Task TwoCompanies_EachMemberSeesOnlyTheirOwnCompany()
    {
        var tenantA = await SeedTenantAsync("Empresa A");
        var tenantB = await SeedTenantAsync("Empresa B");
        var ana = await SeedAsync(tenantA, "Ana", "ana@a.test", TenantRole.Owner);
        var bruno = await SeedAsync(tenantB, "Bruno", "bruno@b.test", TenantRole.Owner);

        var asAna = await AskAsync(ana, tenantA, TenantRole.Owner);
        var asBruno = await AskAsync(bruno, tenantB, TenantRole.Owner);

        Assert.Equal(("Empresa A", tenantA, "ana@a.test"), (asAna.TenantName, asAna.TenantId, asAna.Email));
        Assert.Equal(("Empresa B", tenantB, "bruno@b.test"), (asBruno.TenantName, asBruno.TenantId, asBruno.Email));
    }

    [Fact]
    public async Task AMembershipOfAnotherCompany_IsInvisible_EvenIfTheTokenClaimsIt()
    {
        // El filtro de empresa de la base de datos actúa aunque la consulta se equivocara: el contexto está abierto para A, así que
        // la pertenencia de Bruno (de B) no existe para él, y por tanto no sale ni su ficha ni su empresa.
        var tenantA = await SeedTenantAsync("Empresa A2");
        var tenantB = await SeedTenantAsync("Empresa B2");
        await SeedAsync(tenantA, "Ana", "ana2@a.test", TenantRole.Owner);
        var bruno = await SeedAsync(tenantB, "Bruno", "bruno2@b.test", TenantRole.Owner);

        await Assert.ThrowsAsync<ForbiddenException>(() => AskAsync(bruno, activeTenant: tenantA, TenantRole.Owner));
    }

    [Fact]
    public async Task APersonWithoutMembership_GetsNothing_EvenIfTheirRecordExists()
    {
        var tenant = await SeedTenantAsync("Empresa sin esa persona");
        var orphan = User.Create(Guid.NewGuid(), "Huérfana", Email.Create("huerfana@x.test"));
        await using (var db = _fixture.CreateDbContext(tenant))
        {
            db.Users.Add(orphan);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            AskAsync(new Person(orphan.Id, tenant, Guid.NewGuid()), tenant, TenantRole.Owner));
    }

    [Fact]
    public async Task TheQueryIsOneSingleStatement_NotOnePerTable()
    {
        var tenant = await SeedTenantAsync("Empresa de una consulta");
        var ana = await SeedAsync(tenant, "Ana", "ana@una.test", TenantRole.Owner);

        var statements = new List<string>();
        await using var db = _fixture.CreateDbContext(tenant, options => options.AddInterceptors(new StatementRecorder(statements)));
        await new GetCurrentAccountQueryHandler(db, new TestCurrentUser(TenantRole.Owner, ana.UserId, tenant))
            .Handle(new GetCurrentAccountQuery(), CancellationToken.None);

        Assert.Single(statements);
    }

    private sealed class StatementRecorder(List<string> statements) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override System.Threading.Tasks.ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

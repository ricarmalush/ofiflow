using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Si el rol efectivo exige el segundo factor y la cuenta no lo tiene activo (spec 022 R1, R2, R5), contra SQL Server real: lo decide la base de datos (el rol efectivo y una credencial
/// <b>confirmada</b> de <b>esta</b> cuenta) y viaja en la <b>misma consulta</b> que la pertenencia: no cuesta ninguna ida más.
/// </summary>
public class CurrentUserSecondFactorTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class NobodyRequiresIt : ISecondFactorRequirement
    {
        public bool IsRequiredFor(TenantRole role) => false;
    }

    private (CurrentUser User, QueryCounter Queries, ApplicationDbContext Db) Request(Guid userId, Guid tenantId, string? tokenRole = null, ISecondFactorRequirement? requirement = null)
    {
        var queries = new QueryCounter();
        var db = _fixture.CreateDbContext(tenantId, builder => builder.AddInterceptors(queries));

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()), new("tenant_id", tenantId.ToString()) };
        if (tokenRole is not null)
        {
            claims.Add(new Claim("role", tokenRole));
        }

        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };

        return (new CurrentUser(accessor, db, requirement ?? new SecondFactorRequirement()), queries, db);
    }

    public enum Factor
    {
        None,
        Pending,
        Confirmed
    }

    /// <summary>Un miembro de una empresa real (con su fila en <c>Tenants</c>), con el factor en el estado indicado.</summary>
    private async Task<(Guid TenantId, Guid UserId)> SeedAsync(TenantRole role, Factor factor, bool suspended = false)
    {
        var tenant = Tenant.Create("Fontanería Pérez");
        if (suspended)
        {
            tenant.Suspend(Now);
        }

        var userId = Guid.NewGuid();

        await using var db = _fixture.CreateDbContext(tenant.Id);
        db.Tenants.Add(tenant);
        db.TenantUsers.Add(TenantUser.Create(tenant.Id, userId, role));
        await AddFactorAsync(db, userId, factor);
        await db.SaveChangesAsync(CancellationToken.None);

        return (tenant.Id, userId);
    }

    private static Task AddFactorAsync(ApplicationDbContext db, Guid userId, Factor factor)
    {
        switch (factor)
        {
            case Factor.Pending:
                db.MfaCredentials.Add(MfaCredential.CreatePending(userId, "cifrado-pendiente"));
                break;
            case Factor.Confirmed:
                db.MfaCredentials.Add(MfaCredential.CreateConfirmed(userId, "cifrado-confirmado", 1, Now));
                break;
        }

        return Task.CompletedTask;
    }

    // ===== Quién debe activarlo =====

    [Theory]
    [InlineData(TenantRole.Owner, Factor.None, true)]
    [InlineData(TenantRole.Admin, Factor.None, true)]
    [InlineData(TenantRole.Owner, Factor.Pending, true)]   // un secreto pedido y sin confirmar no protege nada: no libra de la obligación
    [InlineData(TenantRole.Admin, Factor.Pending, true)]
    [InlineData(TenantRole.Owner, Factor.Confirmed, false)]
    [InlineData(TenantRole.Admin, Factor.Confirmed, false)]
    [InlineData(TenantRole.Manager, Factor.None, false)]    // los roles que no lo exigen siguen como siempre
    [InlineData(TenantRole.Technician, Factor.None, false)]
    [InlineData(TenantRole.Employee, Factor.None, false)]
    [InlineData(TenantRole.Manager, Factor.Confirmed, false)]
    public async Task TheFlagIsTrue_OnlyForARoleThatRequiresItWithoutAConfirmedFactor(TenantRole role, Factor factor, bool required)
    {
        var member = await SeedAsync(role, factor);
        var (user, _, db) = Request(member.UserId, member.TenantId);
        await using (db)
        {
            Assert.True(await user.VerifyMembershipAsync(CancellationToken.None));

            Assert.Equal(role, user.Role);
            Assert.Equal(required, user.SecondFactorEnrollmentRequired);
        }
    }

    [Fact]
    public async Task TheFlagCountsOnlyTheFactorOfThisAccount_NotAnotherOnes()
    {
        // Otra cuenta (de otra empresa) tiene el factor confirmado: no libra a esta de nada. Y viceversa: que esta no lo tenga no afecta a la otra.
        var withFactor = await SeedAsync(TenantRole.Owner, Factor.Confirmed);
        var without = await SeedAsync(TenantRole.Owner, Factor.None);

        var (userWith, _, dbWith) = Request(withFactor.UserId, withFactor.TenantId);
        var (userWithout, _, dbWithout) = Request(without.UserId, without.TenantId);
        await using (dbWith)
        await using (dbWithout)
        {
            await userWith.VerifyMembershipAsync(CancellationToken.None);
            await userWithout.VerifyMembershipAsync(CancellationToken.None);

            Assert.False(userWith.SecondFactorEnrollmentRequired);
            Assert.True(userWithout.SecondFactorEnrollmentRequired);
        }
    }

    [Fact]
    public async Task AFactorConfirmedByTheSamePersonInAnotherCompanyContext_IsStillTheirOwn()
    {
        // La credencial es de la cuenta, no de la empresa: no hay filtro de empresa que la oculte. Se consulta con el contexto de la empresa de la persona y se ve.
        var member = await SeedAsync(TenantRole.Admin, Factor.Confirmed);
        var (user, _, db) = Request(member.UserId, member.TenantId);
        await using (db)
        {
            await user.VerifyMembershipAsync(CancellationToken.None);

            Assert.False(user.SecondFactorEnrollmentRequired);
        }
    }

    // ===== Se decide con el rol EFECTIVO =====

    [Fact]
    public async Task TheEffectiveRoleDecides_NotTheOneThatTheTokenSays()
    {
        // La base de datos dice Technician (no lo exige) aunque el token diga Owner: no se le obliga.
        var technician = await SeedAsync(TenantRole.Technician, Factor.None);
        var (userA, _, dbA) = Request(technician.UserId, technician.TenantId, tokenRole: "Owner");

        // La base de datos dice Owner (lo exige) aunque el token diga Technician: se le obliga.
        var owner = await SeedAsync(TenantRole.Owner, Factor.None);
        var (userB, _, dbB) = Request(owner.UserId, owner.TenantId, tokenRole: "Technician");

        await using (dbA)
        await using (dbB)
        {
            await userA.VerifyMembershipAsync(CancellationToken.None);
            await userB.VerifyMembershipAsync(CancellationToken.None);

            Assert.False(userA.SecondFactorEnrollmentRequired);
            Assert.True(userB.SecondFactorEnrollmentRequired);
        }
    }

    [Fact]
    public async Task APromotionTakesEffectOnTheNextRequest_WithTheSameToken()
    {
        var member = await SeedAsync(TenantRole.Technician, Factor.None);

        var (before, _, dbBefore) = Request(member.UserId, member.TenantId, tokenRole: "Technician");
        await using (dbBefore)
        {
            await before.VerifyMembershipAsync(CancellationToken.None);
            Assert.False(before.SecondFactorEnrollmentRequired);
        }

        // Un gestor la promueve a Admin mientras su token sigue vivo (el token sigue diciendo Technician).
        await using (var manager = _fixture.CreateDbContext(member.TenantId))
        {
            var membership = manager.TenantUsers.Single(tu => tu.UserId == member.UserId);
            membership.ChangeRole(TenantRole.Admin);
            await manager.SaveChangesAsync(CancellationToken.None);
        }

        var (after, _, dbAfter) = Request(member.UserId, member.TenantId, tokenRole: "Technician");
        await using (dbAfter)
        {
            await after.VerifyMembershipAsync(CancellationToken.None);
            Assert.True(after.SecondFactorEnrollmentRequired);
        }
    }

    // ===== Fail-closed: sin verificar, o sin rol =====

    [Fact]
    public async Task UntilTheMembershipIsVerified_TheFlagIsFalse_ItIsNotReadWithoutVerifying()
    {
        var member = await SeedAsync(TenantRole.Owner, Factor.None);
        var (user, queries, db) = Request(member.UserId, member.TenantId);
        await using (db)
        {
            Assert.False(user.SecondFactorEnrollmentRequired);
            Assert.Equal(0, queries.Count);
        }
    }

    [Fact]
    public async Task WhoIsNoLongerAMember_HasNoFlag_TheRequestIsDeniedForAnotherReason()
    {
        var (user, _, db) = Request(Guid.NewGuid(), Guid.NewGuid());
        await using (db)
        {
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));

            Assert.False(user.SecondFactorEnrollmentRequired);
        }
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_HasNoFlag_ThereIsNoEffectiveRole()
    {
        var member = await SeedAsync(TenantRole.Owner, Factor.None, suspended: true);
        var (user, _, db) = Request(member.UserId, member.TenantId);
        await using (db)
        {
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));

            Assert.True(user.TenantSuspended);
            Assert.False(user.SecondFactorEnrollmentRequired);
        }
    }

    // ===== El coste (R5) =====

    [Theory]
    [InlineData(Factor.None)]
    [InlineData(Factor.Pending)]
    [InlineData(Factor.Confirmed)]
    public async Task TheFlagCostsNoExtraQuery_ItTravelsInTheMembershipOne(Factor factor)
    {
        var member = await SeedAsync(TenantRole.Owner, factor);
        var (user, queries, db) = Request(member.UserId, member.TenantId);
        await using (db)
        {
            await user.VerifyMembershipAsync(CancellationToken.None);
            _ = user.SecondFactorEnrollmentRequired;
            await user.VerifyMembershipAsync(CancellationToken.None);
            await user.VerifyMembershipAsync(CancellationToken.None);

            Assert.Equal(1, queries.Count);
        }
    }

    // ===== La regla viene de la implementación registrada =====

    [Fact]
    public async Task TheFlagFollowsTheRegisteredRequirement_NotACopyOfTheRule()
    {
        // La puerta consulta la regla por la interfaz: con una implementación que no exige a nadie, un Owner sin factor no tiene la marca. (Es lo que usan los tests de la API
        // que no tratan de esto; en producción siempre se registra la regla real.)
        var member = await SeedAsync(TenantRole.Owner, Factor.None);
        var (user, _, db) = Request(member.UserId, member.TenantId, requirement: new NobodyRequiresIt());
        await using (db)
        {
            await user.VerifyMembershipAsync(CancellationToken.None);

            Assert.False(user.SecondFactorEnrollmentRequired);
        }
    }
}

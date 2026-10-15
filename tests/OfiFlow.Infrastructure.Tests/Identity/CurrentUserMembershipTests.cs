using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La revalidación del rol y de la pertenencia en cada petición (spec 009 R16 y R17), contra SQL Server real: lo que
/// decide es la base de datos, no el token, y cuesta exactamente una consulta.
/// </summary>
public class CurrentUserMembershipTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    /// <summary>Cuenta las consultas que se ejecutan contra la base de datos.</summary>
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

    /// <summary>Una "petición": su propio contexto de base de datos y su propio CurrentUser, con las claims de un token.</summary>
    private (CurrentUser User, QueryCounter Queries, ApplicationDbContext Db) Request(Guid? userId, Guid? tenantId, string? tokenRole = null, string? rawTenantClaim = null)
    {
        var queries = new QueryCounter();
        var db = _fixture.CreateDbContext(tenantId ?? Guid.Empty, builder => builder.AddInterceptors(queries));

        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId.Value.ToString()));
        }

        if (rawTenantClaim is not null)
        {
            claims.Add(new Claim("tenant_id", rawTenantClaim));
        }
        else if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        if (tokenRole is not null)
        {
            claims.Add(new Claim("role", tokenRole));
        }

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };

        return (new CurrentUser(accessor, db), queries, db);
    }

    private async Task<(Guid TenantId, Guid UserId, Guid MembershipId)> SeedMemberAsync(TenantRole role)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var db = _fixture.CreateDbContext(tenantId);
        var membership = TenantUser.Create(tenantId, userId, role);
        db.TenantUsers.Add(membership);
        await db.SaveChangesAsync(CancellationToken.None);

        return (tenantId, userId, membership.Id);
    }

    // ===== Lo que decide es la base de datos =====

    [Fact]
    public async Task UntilTheMembershipIsVerified_ThereIsNoRole()
    {
        // Fail-closed: quien lea el rol sin haber verificado no ve ninguno, aunque el token diga Owner.
        var member = await SeedMemberAsync(TenantRole.Owner);
        var (user, queries, db) = Request(member.UserId, member.TenantId, tokenRole: "Owner");
        await using (db)
        {
            Assert.Null(user.Role);
            Assert.Equal(TenantRole.Owner, user.TokenRole); // lo que dice el token se lee, pero no autoriza
            Assert.Equal(0, queries.Count);
        }
    }

    [Fact]
    public async Task TheEffectiveRole_IsTheOneInTheDatabase_NotTheOneInTheToken()
    {
        var member = await SeedMemberAsync(TenantRole.Technician);
        var (user, _, db) = Request(member.UserId, member.TenantId, tokenRole: "Owner"); // el token dice Owner
        await using (db)
        {
            Assert.True(await user.VerifyMembershipAsync(CancellationToken.None));

            Assert.Equal(TenantRole.Technician, user.Role); // pero la base de datos dice Technician
            Assert.Equal(TenantRole.Owner, user.TokenRole);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Superadmin")]
    [InlineData("1")]
    public async Task TheRoleClaim_HasNoEffect_WhateverItSays(string? claim)
    {
        // Antes (spec 008) un token sin un rol válido se denegaba. Ahora el claim es solo informativo: el rol sale de la
        // base de datos, así que un claim ausente o raro no cambia nada (ni da poderes, ni los quita).
        var member = await SeedMemberAsync(TenantRole.Manager);
        var (user, _, db) = Request(member.UserId, member.TenantId, tokenRole: claim);
        await using (db)
        {
            Assert.True(await user.VerifyMembershipAsync(CancellationToken.None));

            Assert.Equal(TenantRole.Manager, user.Role);
        }
    }

    // ===== Quitar o degradar surte efecto en la siguiente petición =====

    [Fact]
    public async Task APersonWhoWasRemoved_IsNotVerified_AndHasNoRole_EvenWithAValidToken()
    {
        var member = await SeedMemberAsync(TenantRole.Technician);

        // Se le quita mientras su token sigue vivo.
        await using (var admin = _fixture.CreateDbContext(member.TenantId))
        {
            admin.TenantUsers.Remove(await admin.TenantUsers.SingleAsync(tu => tu.Id == member.MembershipId));
            await admin.SaveChangesAsync(CancellationToken.None);
        }

        var (user, _, db) = Request(member.UserId, member.TenantId, tokenRole: "Technician");
        await using (db)
        {
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));
            Assert.Null(user.Role);
        }
    }

    [Fact]
    public async Task ARoleChangeInTheDatabase_IsSeenByTheNextRequest_WithoutANewToken()
    {
        var member = await SeedMemberAsync(TenantRole.Technician);

        var (first, _, firstDb) = Request(member.UserId, member.TenantId, tokenRole: "Technician");
        await using (firstDb)
        {
            await first.VerifyMembershipAsync(CancellationToken.None);
            Assert.Equal(TenantRole.Technician, first.Role);
        }

        // Alguien lo asciende. El token es el mismo, no se ha renovado nada.
        await using (var admin = _fixture.CreateDbContext(member.TenantId))
        {
            (await admin.TenantUsers.SingleAsync(tu => tu.Id == member.MembershipId)).ChangeRole(TenantRole.Manager);
            await admin.SaveChangesAsync(CancellationToken.None);
        }

        var (second, _, secondDb) = Request(member.UserId, member.TenantId, tokenRole: "Technician");
        await using (secondDb)
        {
            await second.VerifyMembershipAsync(CancellationToken.None);
            Assert.Equal(TenantRole.Manager, second.Role); // ya lo ve, en la siguiente petición
        }
    }

    // ===== Solo cuenta la pertenencia a ESA empresa =====

    [Fact]
    public async Task AMembershipInAnotherTenant_DoesNotCount()
    {
        var a = await SeedMemberAsync(TenantRole.Owner);
        var otherTenant = Guid.NewGuid();

        // La persona es Owner de su empresa, pero el token apunta a otra en la que no pertenece.
        var (user, _, db) = Request(a.UserId, otherTenant, tokenRole: "Owner");
        await using (db)
        {
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));
            Assert.Null(user.Role);
        }
    }

    [Fact]
    public async Task AnotherPersonsMembership_IsNeverUsed()
    {
        var owner = await SeedMemberAsync(TenantRole.Owner);

        // Otra persona, con el tenant del Owner pero sin pertenencia propia: no hereda su rol.
        var (user, _, db) = Request(Guid.NewGuid(), owner.TenantId, tokenRole: "Owner");
        await using (db)
        {
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));
            Assert.Null(user.Role);
        }
    }

    // ===== Un token sin la identidad completa =====

    [Fact]
    public async Task ATokenWithoutUserOrTenant_IsNotVerified_WithoutTouchingTheDatabase()
    {
        var member = await SeedMemberAsync(TenantRole.Owner);

        var noUser = Request(userId: null, member.TenantId, tokenRole: "Owner");
        var noTenant = Request(member.UserId, tenantId: null, tokenRole: "Owner");
        var badTenant = Request(member.UserId, tenantId: null, tokenRole: "Owner", rawTenantClaim: "no-es-un-guid");

        foreach (var (user, queries, db) in new[] { noUser, noTenant, badTenant })
        {
            await using (db)
            {
                Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));
                Assert.Null(user.Role);
                Assert.Equal(0, queries.Count); // ni siquiera una consulta
            }
        }
    }

    // ===== El coste: una sola consulta por petición (R17) =====

    [Fact]
    public async Task VerifyingCostsExactlyOneQuery_EvenIfAskedSeveralTimes()
    {
        var member = await SeedMemberAsync(TenantRole.Manager);
        var (user, queries, db) = Request(member.UserId, member.TenantId);
        await using (db)
        {
            await user.VerifyMembershipAsync(CancellationToken.None);
            await user.VerifyMembershipAsync(CancellationToken.None);
            await user.VerifyMembershipAsync(CancellationToken.None);

            Assert.Equal(1, queries.Count);
            Assert.Equal(TenantRole.Manager, user.Role);
        }
    }

    [Fact]
    public async Task ARemovedPersonsVerification_IsAlsoCached_AndStaysDenied()
    {
        var (user, queries, db) = Request(Guid.NewGuid(), Guid.NewGuid());
        await using (db)
        {
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));
            Assert.False(await user.VerifyMembershipAsync(CancellationToken.None));

            Assert.Equal(1, queries.Count);
        }
    }
}

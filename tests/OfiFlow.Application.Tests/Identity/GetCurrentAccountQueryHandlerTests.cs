using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Queries.GetCurrentAccount;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Quién soy (spec 012): los datos de quien pregunta, su empresa y su rol EFECTIVO, y nada de nadie más. Que una empresa no vea
/// nada de otra, con el filtro real, lo prueba contra SQL Server <c>GetCurrentAccountTests</c> (esta base en memoria no tiene filtro
/// de empresa, y por eso la consulta repite el Id de empresa a propósito: aquí se prueba que eso solo ya basta).
/// </summary>
public class GetCurrentAccountQueryHandlerTests
{
    private readonly TestDbContext _db = TestDbContextFactory.Create();

    private sealed record Seeded(Guid UserId, Guid TenantId, Guid MembershipId);

    private async Task<Seeded> SeedAsync(string tenantName, string userName, string email, TenantRole role)
    {
        var tenant = Tenant.Create(tenantName);
        var user = User.Create(Guid.NewGuid(), userName, Email.Create(email));
        var membership = TenantUser.Create(tenant.Id, user.Id, role);

        _db.Tenants.Add(tenant);
        _db.Users.Add(user);
        _db.TenantUsers.Add(membership);
        await _db.SaveChangesAsync(CancellationToken.None);

        return new Seeded(user.Id, tenant.Id, membership.Id);
    }

    private Task<CurrentAccountDto> AskAsync(Seeded who, TenantRole effectiveRole, TenantRole? tokenRole = null) =>
        new GetCurrentAccountQueryHandler(_db, new FakeCurrentUser(effectiveRole, who.UserId, who.TenantId, tokenRole))
            .Handle(new GetCurrentAccountQuery(), CancellationToken.None);

    [Fact]
    public async Task ReturnsTheDataOfWhoAsks_WithTheStablePermissionNames()
    {
        var ana = await SeedAsync("Fontanería A", "Ana Pérez", "ana@a.com", TenantRole.Technician);

        var account = await AskAsync(ana, TenantRole.Technician);

        Assert.Equal(ana.UserId, account.UserId);
        Assert.Equal(ana.MembershipId, account.MembershipId);
        Assert.Equal("Ana Pérez", account.Name);
        Assert.Equal("ana@a.com", account.Email);
        Assert.Equal(ana.TenantId, account.TenantId);
        Assert.Equal("Fontanería A", account.TenantName);
        Assert.Equal("Technician", account.Role);
        Assert.Equal(["Account.Read", "Customers.Read", "Jobs.Execute", "Jobs.Read", "Mfa.Manage"], account.Permissions);
    }

    [Fact]
    public async Task TheRoleAndThePermissions_AreTheEffectiveOnes_NotTheOnesTheTokenSaid()
    {
        // R2: tras un cambio de rol, el token sigue diciendo el viejo (Technician) pero la base de datos ya dice Manager.
        var ana = await SeedAsync("Fontanería A", "Ana", "ana@a.com", TenantRole.Manager);

        var account = await AskAsync(ana, effectiveRole: TenantRole.Manager, tokenRole: TenantRole.Technician);

        Assert.Equal("Manager", account.Role);
        Assert.Equal(PermissionNames.For(TenantRole.Manager), account.Permissions);
        Assert.Contains("Jobs.Assign", account.Permissions); // un permiso que el rol del token no tenía
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task ThePermissionsOfEveryRole_AreExactlyTheOnesInTheSingleTable(TenantRole role)
    {
        var who = await SeedAsync("Empresa", "Persona", "persona@a.com", role);

        var account = await AskAsync(who, role);

        Assert.Equal(RolePermissions.For(role).Count, account.Permissions.Count);
        Assert.All(account.Permissions, name => Assert.Contains(RolePermissions.For(role), p => PermissionNames.Of(p) == name));
    }

    [Fact]
    public async Task WithSeveralPeopleAndCompanies_ItIsAlwaysTheOneWhoAsks()
    {
        var ana = await SeedAsync("Empresa de Ana", "Ana", "ana@a.com", TenantRole.Owner);
        var bruno = await SeedAsync("Empresa de Bruno", "Bruno", "bruno@b.com", TenantRole.Technician);

        var asAna = await AskAsync(ana, TenantRole.Owner);
        var asBruno = await AskAsync(bruno, TenantRole.Technician);

        Assert.Equal(("Ana", "Empresa de Ana", "ana@a.com"), (asAna.Name, asAna.TenantName, asAna.Email));
        Assert.Equal(("Bruno", "Empresa de Bruno", "bruno@b.com"), (asBruno.Name, asBruno.TenantName, asBruno.Email));
    }

    [Fact]
    public async Task APersonWhoseMembershipIsInAnotherCompany_GetsNothingFromThatCompany()
    {
        // Un token con la empresa A y una pertenencia que solo existe en B: no hay respuesta, y desde luego no los datos de B.
        var ana = await SeedAsync("Empresa A", "Ana", "ana@a.com", TenantRole.Owner);
        var bruno = await SeedAsync("Empresa B", "Bruno", "bruno@b.com", TenantRole.Owner);
        var mixed = new Seeded(bruno.UserId, ana.TenantId, bruno.MembershipId);

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => AskAsync(mixed, TenantRole.Owner));

        Assert.Equal(TenancyErrors.Forbidden, denied.Code);
    }

    [Theory]
    [InlineData("sin-usuario")]
    [InlineData("sin-empresa")]
    [InlineData("sin-rol")]
    public async Task WithoutTheIdentityTheCompanyOrTheVerifiedRole_ItIsDenied(string missing)
    {
        var ana = await SeedAsync("Empresa A", "Ana", "ana@a.com", TenantRole.Owner);
        var user = new FakeCurrentUser(
            missing == "sin-rol" ? null : TenantRole.Owner,
            missing == "sin-usuario" ? null : ana.UserId,
            missing == "sin-empresa" ? null : ana.TenantId);

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() =>
            new GetCurrentAccountQueryHandler(_db, user).Handle(new GetCurrentAccountQuery(), CancellationToken.None));

        Assert.Equal(TenancyErrors.Forbidden, denied.Code);
    }

    [Fact]
    public async Task ThereIsNoMembership_ItIsDenied()
    {
        var ghost = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<ForbiddenException>(() => AskAsync(ghost, TenantRole.Owner));
    }

    [Fact]
    public void TheAnswerHasExactlyTheseFields_NothingSensitiveCanBeAddedByAccident()
    {
        // NEVER de la spec: ni hash de contraseña, ni secreto TOTP, ni códigos de recuperación, ni tokens. Si alguien añade una
        // propiedad, este test falla y obliga a decidir, a propósito, si es algo que la persona debe recibir.
        var properties = typeof(CurrentAccountDto).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(["Email", "MembershipId", "Name", "Permissions", "Role", "TenantId", "TenantName", "UserId"], properties);
    }

    [Fact]
    public void TheQueryHasNoParameters_SoNobodyCanAskForAnotherPerson()
    {
        // R1: la cuenta sale del token, nunca de la petición. Sin propiedades no hay un Id que falsificar ni mass assignment posible.
        Assert.Empty(typeof(GetCurrentAccountQuery).GetProperties().Where(p => p.DeclaringType == typeof(GetCurrentAccountQuery)));
    }
}

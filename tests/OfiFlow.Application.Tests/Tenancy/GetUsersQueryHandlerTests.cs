using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tenancy.Queries.GetUsers;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

public class GetUsersQueryHandlerTests : MemberManagementTestBase
{
    private GetUsersQueryHandler Handler(TenantRole viewer) => new(Db, Actor(viewer));

    [Fact]
    public async Task ItListsEveryMember_WithTheirNameEmailRoleAndJoinDate()
    {
        var joined = Now.AddDays(-3);
        var ana = await SeedMemberAsync(TenantRole.Technician, "Ana Pérez", "ana@example.com", joinedAt: joined);

        var listed = await Handler(TenantRole.Manager).Handle(new GetUsersQuery(), CancellationToken.None);

        var dto = Assert.Single(listed);
        Assert.Equal(ana.Id, dto.Id); // el de la pertenencia: es el que se usa para gestionarlo y asignarle trabajos
        Assert.Equal("Ana Pérez", dto.Name);
        Assert.Equal("ana@example.com", dto.Email);
        Assert.Equal("Technician", dto.Role);
        Assert.Equal(joined, dto.JoinedAt);
    }

    [Fact]
    public async Task TheMembers_AreOrderedByName()
    {
        await SeedMemberAsync(TenantRole.Employee, "Zoe");
        await SeedMemberAsync(TenantRole.Employee, "Ana");
        await SeedMemberAsync(TenantRole.Employee, "Marta");

        var listed = await Handler(TenantRole.Owner).Handle(new GetUsersQuery(), CancellationToken.None);

        Assert.Equal(["Ana", "Marta", "Zoe"], listed.Select(m => m.Name));
    }

    [Fact]
    public async Task TheCurrentUser_IsMarked_AndNobodyElseIs()
    {
        await SeedActorAsync(TenantRole.Admin, "Yo");
        await SeedMemberAsync(TenantRole.Technician, "Otra");

        var listed = await Handler(TenantRole.Admin).Handle(new GetUsersQuery(), CancellationToken.None);

        Assert.Equal("Yo", Assert.Single(listed, m => m.IsCurrentUser).Name);
        Assert.Equal(1, listed.Count(m => !m.IsCurrentUser));
    }

    [Fact]
    public async Task APersonWithoutMembership_IsNotListed()
    {
        // Una ficha suelta (sin pertenencia a la empresa) no es un miembro: solo se llega a la ficha desde la pertenencia.
        await SeedMemberAsync(TenantRole.Technician, "Miembro");
        Db.Users.Add(OfiFlow.Domain.Identity.User.Create(Guid.NewGuid(), "Sin pertenencia", OfiFlow.Domain.Common.Email.Create("suelta@example.com")));
        await Db.SaveChangesAsync(CancellationToken.None);

        var listed = await Handler(TenantRole.Owner).Handle(new GetUsersQuery(), CancellationToken.None);

        Assert.Equal(["Miembro"], listed.Select(m => m.Name));
    }

    [Fact]
    public void TheDto_DoesNotExposeTheAccountId()
    {
        // Solo el Id de la pertenencia: el de la cuenta (UserId) no sale hacia la API.
        var properties = typeof(TenantUserDto).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("UserId", properties);
        Assert.DoesNotContain(properties, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }
}

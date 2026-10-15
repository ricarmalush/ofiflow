using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Tenancy.Commands.ChangeUserRole;
using OfiFlow.Application.Tenancy.Commands.RemoveUser;
using OfiFlow.Application.Tenancy.Queries.GetUsers;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Listar, cambiar el rol y quitar miembros con los handlers reales, el filtro de empresa real y SQL Server real
/// (spec 009). Los tests de Application usan una base en memoria SIN filtro de empresa: no pueden demostrar que una
/// empresa no ve ni toca a los miembros de otra, que es lo más importante de estas operaciones.
/// </summary>
public class MemberManagementIntegrationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "Password123!";

    private static readonly JwtOptions Jwt = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    private readonly SqlServerContainerFixture _fixture = fixture;

    private static IdentityService Identity(ApplicationDbContext db) => new(db, NullLogger<IdentityService>.Instance);

    private static TokenService Tokens(ApplicationDbContext db) =>
        new(db, Options.Create(Jwt), NullLogger<TokenService>.Instance);

    /// <summary>Una empresa con su Owner (con cuenta real) y los miembros indicados. Devuelve los datos para actuar y comprobar.</summary>
    private async Task<(Guid TenantId, Guid OwnerUserId, Dictionary<string, TenantUser> Members)> SeedTenantAsync(params (string Name, TenantRole Role)[] members)
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var created = new Dictionary<string, TenantUser>();

        await using var db = _fixture.CreateDbContext(tenantId);

        await Identity(db).CreateUserAsync(ownerUserId, $"{ownerUserId:N}@example.com", Password, CancellationToken.None);
        db.Users.Add(User.Create(ownerUserId, "Dueña", Email.Create($"{ownerUserId:N}@example.com")));
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, ownerUserId));

        foreach (var (name, role) in members)
        {
            var userId = Guid.NewGuid();
            var email = $"{userId:N}@example.com";
            await Identity(db).CreateUserAsync(userId, email, Password, CancellationToken.None);
            db.Users.Add(User.Create(userId, name, Email.Create(email)));

            var membership = TenantUser.Create(tenantId, userId, role);
            db.TenantUsers.Add(membership);
            created[name] = membership;
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return (tenantId, ownerUserId, created);
    }

    // ===== Listar: una empresa solo ve a los suyos =====

    [Fact]
    public async Task ATenantOnlySeesItsOwnMembers_EvenThoughThePeopleTableIsShared()
    {
        var a = await SeedTenantAsync(("Ana de A", TenantRole.Technician), ("Berta de A", TenantRole.Employee));
        var b = await SeedTenantAsync(("Carlos de B", TenantRole.Manager));

        await using var dbA = _fixture.CreateDbContext(a.TenantId);
        var seenByA = await new GetUsersQueryHandler(dbA, new TestCurrentUser(TenantRole.Owner, a.OwnerUserId, a.TenantId))
            .Handle(new GetUsersQuery(), CancellationToken.None);

        Assert.Equal(["Ana de A", "Berta de A", "Dueña"], seenByA.Select(m => m.Name));
        Assert.DoesNotContain(seenByA, m => m.Name == "Carlos de B");

        // Control positivo: la otra empresa sí ve a los suyos, así que "no aparece" no se debe a un fallo al sembrar.
        await using var dbB = _fixture.CreateDbContext(b.TenantId);
        var seenByB = await new GetUsersQueryHandler(dbB, new TestCurrentUser(TenantRole.Owner, b.OwnerUserId, b.TenantId))
            .Handle(new GetUsersQuery(), CancellationToken.None);
        Assert.Contains(seenByB, m => m.Name == "Carlos de B");
        Assert.DoesNotContain(seenByB, m => m.Name == "Ana de A");
    }

    // ===== Cambiar el rol / quitar con un miembro de otra empresa =====

    [Fact]
    public async Task ChangingTheRoleOfAnotherTenantsMember_IsNotFound_AndTheirRoleDoesNotChange()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync(("Carlos de B", TenantRole.Employee));
        var victim = b.Members["Carlos de B"];

        await using (var dbA = _fixture.CreateDbContext(a.TenantId))
        {
            var handler = new ChangeUserRoleCommandHandler(dbA, new TestCurrentUser(TenantRole.Owner, a.OwnerUserId, a.TenantId), NullLogger<ChangeUserRoleCommandHandler>.Instance);

            var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
                handler.Handle(new ChangeUserRoleCommand(victim.Id, TenantRole.Admin), CancellationToken.None));

            Assert.Equal(TenancyErrors.TenantUserNotFound, exception.Code); // el mismo error que un Id inexistente
        }

        await using var check = _fixture.CreateDbContext(b.TenantId);
        Assert.Equal(TenantRole.Employee, (await check.TenantUsers.SingleAsync(tu => tu.Id == victim.Id)).Role);
    }

    [Fact]
    public async Task RemovingAnotherTenantsMember_IsNotFound_AndNothingOfThemIsDeleted()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync(("Carlos de B", TenantRole.Employee));
        var victim = b.Members["Carlos de B"];

        await using (var dbA = _fixture.CreateDbContext(a.TenantId))
        {
            var handler = new RemoveUserCommandHandler(
                dbA, new TestCurrentUser(TenantRole.Owner, a.OwnerUserId, a.TenantId), Identity(dbA), Tokens(dbA), NullLogger<RemoveUserCommandHandler>.Instance);

            await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new RemoveUserCommand(victim.Id), CancellationToken.None));
        }

        // Ni su pertenencia, ni su ficha, ni su cuenta han desaparecido.
        await using var check = _fixture.CreateDbContext(b.TenantId);
        Assert.Equal(1, await check.TenantUsers.CountAsync(tu => tu.Id == victim.Id));
        Assert.Equal(1, await check.Users.CountAsync(u => u.Id == victim.UserId));
        Assert.Equal(1, await check.ApplicationUsers.CountAsync(u => u.Id == victim.UserId));
    }

    // ===== La baja completa con los servicios reales (R14) =====

    [Fact]
    public async Task ARemoval_WithTheRealServices_LeavesNoTraceOfTheMember_AndKeepsTheirFinishedJobs()
    {
        var tenant = await SeedTenantAsync(("Ana", TenantRole.Technician), ("Berta", TenantRole.Technician));
        var target = tenant.Members["Ana"];
        var bystander = tenant.Members["Berta"];
        var email = $"{target.UserId:N}@example.com";

        Guid openJobId, finishedJobId, bystanderJobId;
        string refreshToken;

        await using (var seed = _fixture.CreateDbContext(tenant.TenantId))
        {
            var open = Job.Create(tenant.TenantId, Guid.NewGuid(), "Abierto", null, JobPriority.Normal);
            open.AssignTo(target.Id);

            var finished = Job.Create(tenant.TenantId, Guid.NewGuid(), "Terminado", null, JobPriority.Normal);
            finished.AssignTo(target.Id);
            finished.Start();
            finished.Complete();

            var others = Job.Create(tenant.TenantId, Guid.NewGuid(), "De Berta", null, JobPriority.Normal);
            others.AssignTo(bystander.Id);

            seed.Jobs.AddRange(open, finished, others);
            await seed.SaveChangesAsync(CancellationToken.None);
            (openJobId, finishedJobId, bystanderJobId) = (open.Id, finished.Id, others.Id);

            // Una sesión de Ana renovada una vez (queda un token revocado y uno activo), y una de Berta.
            var issued = await Tokens(seed).IssueTokensAsync(target.UserId, tenant.TenantId, TenantRole.Technician, CancellationToken.None);
            var rotated = await Tokens(seed).RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);
            refreshToken = rotated!.RefreshToken;
            await Tokens(seed).IssueTokensAsync(bystander.UserId, tenant.TenantId, TenantRole.Technician, CancellationToken.None);
        }

        await using (var db = _fixture.CreateDbContext(tenant.TenantId))
        {
            var handler = new RemoveUserCommandHandler(
                db, new TestCurrentUser(TenantRole.Owner, tenant.OwnerUserId, tenant.TenantId), Identity(db), Tokens(db), NullLogger<RemoveUserCommandHandler>.Instance);

            await handler.Handle(new RemoveUserCommand(target.Id), CancellationToken.None);
        }

        await using var check = _fixture.CreateDbContext(tenant.TenantId);

        // Nada de Ana en ninguna tabla, ni siquiera los tokens revocados.
        Assert.Equal(0, await check.TenantUsers.CountAsync(tu => tu.Id == target.Id));
        Assert.Equal(0, await check.Users.CountAsync(u => u.Id == target.UserId));
        Assert.Equal(0, await check.ApplicationUsers.CountAsync(u => u.Id == target.UserId));
        Assert.Equal(0, await check.RefreshTokens.CountAsync(rt => rt.UserId == target.UserId));

        // Ya no puede entrar ni renovar la sesión, y su email vuelve a estar libre.
        Assert.Null(await Identity(check).ValidateCredentialsAsync(email, Password, CancellationToken.None));
        Assert.Null(await Tokens(check).RotateRefreshTokenAsync(refreshToken, CancellationToken.None));
        Assert.False(await Identity(check).IsEmailRegisteredAsync(email, CancellationToken.None));

        // Sus trabajos: el abierto queda sin asignar y el terminado conserva la referencia como historial.
        Assert.Null((await check.Jobs.SingleAsync(j => j.Id == openJobId)).AssignedTenantUserId);
        Assert.Equal(target.Id, (await check.Jobs.SingleAsync(j => j.Id == finishedJobId)).AssignedTenantUserId);

        // Control positivo: Berta, y todo lo suyo, intacta.
        Assert.Equal(1, await check.TenantUsers.CountAsync(tu => tu.Id == bystander.Id));
        Assert.Equal(1, await check.ApplicationUsers.CountAsync(u => u.Id == bystander.UserId));
        Assert.Equal(1, await check.RefreshTokens.CountAsync(rt => rt.UserId == bystander.UserId));
        Assert.Equal(bystander.Id, (await check.Jobs.SingleAsync(j => j.Id == bystanderJobId)).AssignedTenantUserId);
    }

    [Fact]
    public async Task ARoleChange_WithTheRealDatabase_IsPersisted()
    {
        var tenant = await SeedTenantAsync(("Ana", TenantRole.Employee));
        var target = tenant.Members["Ana"];

        await using (var db = _fixture.CreateDbContext(tenant.TenantId))
        {
            await new ChangeUserRoleCommandHandler(db, new TestCurrentUser(TenantRole.Owner, tenant.OwnerUserId, tenant.TenantId), NullLogger<ChangeUserRoleCommandHandler>.Instance)
                .Handle(new ChangeUserRoleCommand(target.Id, TenantRole.Manager), CancellationToken.None);
        }

        await using var check = _fixture.CreateDbContext(tenant.TenantId);
        Assert.Equal(TenantRole.Manager, (await check.TenantUsers.SingleAsync(tu => tu.Id == target.Id)).Role);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Identity.Commands.AcceptInvitation;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Aceptar una invitación con el handler real, el filtro de empresa real y SQL Server real (spec 009 R8).
/// Los tests de Application usan una base en memoria SIN filtro de empresa: no pueden demostrar que aceptar
/// funciona cuando todavía no hay empresa activa, que es justo por lo que el handler ignora ese filtro.
/// </summary>
public class AcceptInvitationIntegrationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "Password123!";

    private readonly SqlServerContainerFixture _fixture = fixture;

    /// <summary>
    /// Un contexto SIN empresa activa, como el de una petición anónima: su filtro de tenant compara con
    /// <see cref="Guid.Empty"/>, así que no ve datos de ninguna empresa real.
    /// </summary>
    private Application.Identity.Commands.AcceptInvitation.AcceptInvitationCommandHandler AnonymousHandler(
        out Infrastructure.Persistence.ApplicationDbContext db)
    {
        db = _fixture.CreateDbContext(Guid.Empty);
        return new AcceptInvitationCommandHandler(
            db, new IdentityService(db, NullLogger<IdentityService>.Instance), TimeProvider.System,
            NullLogger<AcceptInvitationCommandHandler>.Instance);
    }

    private async Task<(Guid TenantId, string Token, string Email)> SeedAsync(TenantRole role = TenantRole.Technician)
    {
        var tenantId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.com";
        var (token, hash) = InvitationToken.Generate();

        await using var db = _fixture.CreateDbContext(tenantId);
        db.Invitations.Add(Invitation.Create(tenantId, Email.Create(email), role, hash, Guid.NewGuid(), DateTime.UtcNow));
        await db.SaveChangesAsync(CancellationToken.None);

        return (tenantId, token, email);
    }

    [Fact]
    public async Task AnAnonymousRequest_WithNoActiveTenant_CanStillAcceptItsInvitation()
    {
        var (tenantId, token, email) = await SeedAsync(TenantRole.Manager);

        var handler = AnonymousHandler(out var anonymousDb);
        await using (anonymousDb)
        {
            // Control: con el filtro activo, este contexto sin empresa NO ve la invitación...
            Assert.Empty(await anonymousDb.Invitations.Where(i => i.TenantId == tenantId).ToListAsync());

            // ...y aun así el handler la encuentra, porque la busca por el hash de su token ignorando el filtro.
            await handler.Handle(new AcceptInvitationCommand(token, "Ana Pérez", Password), CancellationToken.None);
        }

        // La cuenta nació en la empresa de la invitación, con su rol, y entra con su contraseña.
        await using var check = _fixture.CreateDbContext(tenantId);
        var membership = await check.TenantUsers.SingleAsync();
        Assert.Equal(TenantRole.Manager, membership.Role);
        Assert.Equal(1, await check.Users.CountAsync(u => u.Id == membership.UserId));
        Assert.NotNull(await new IdentityService(check, NullLogger<IdentityService>.Instance)
            .ValidateCredentialsAsync(email, Password, CancellationToken.None));
        Assert.NotNull((await check.Invitations.SingleAsync()).AcceptedAt);
    }

    [Fact]
    public async Task ARegisteredMembership_BelongsToTheInvitationTenant_AndToNoOtherTenant()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();

        var handler = AnonymousHandler(out var db);
        await using (db)
        {
            await handler.Handle(new AcceptInvitationCommand(first.Token, "Ana Pérez", Password), CancellationToken.None);
        }

        await using var checkFirst = _fixture.CreateDbContext(first.TenantId);
        await using var checkSecond = _fixture.CreateDbContext(second.TenantId);
        Assert.Equal(1, await checkFirst.TenantUsers.CountAsync());
        Assert.Equal(0, await checkSecond.TenantUsers.CountAsync()); // la otra empresa no ganó ningún miembro
        Assert.Null((await checkSecond.Invitations.SingleAsync()).AcceptedAt); // y su invitación sigue pendiente
    }

    [Fact]
    public async Task TwoSimultaneousAcceptances_OfTheSameToken_CreateExactlyOneAccount()
    {
        var (tenantId, token, _) = await SeedAsync();

        // Dos peticiones a la vez con el mismo token. La creación de la cuenta hashea la contraseña (PBKDF2),
        // lo que deja un margen en el que las dos ya han leído la invitación como pendiente: si la base de
        // datos no obligara a que solo una confirme, nacerían dos cuentas.
        async Task<Exception?> AcceptAsync(string name)
        {
            var handler = AnonymousHandler(out var db);
            await using (db)
            {
                try
                {
                    await handler.Handle(new AcceptInvitationCommand(token, name, Password), CancellationToken.None);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
        }

        var results = await Task.WhenAll(Task.Run(() => AcceptAsync("Ana Pérez")), Task.Run(() => AcceptAsync("Otra Persona")));

        Assert.Single(results, result => result is null); // exactamente una ganó
        var loser = Assert.Single(results, result => result is not null);
        var invalid = Assert.IsType<DomainException>(loser); // la otra recibió la respuesta de "token que ya no sirve"
        Assert.Equal(InvitationErrors.Invalid, invalid.Code);

        await using var check = _fixture.CreateDbContext(tenantId);
        Assert.Equal(1, await check.TenantUsers.CountAsync());
        Assert.Equal(1, await check.Users.CountAsync(u => u.Id == check.TenantUsers.Single().UserId));
    }
}

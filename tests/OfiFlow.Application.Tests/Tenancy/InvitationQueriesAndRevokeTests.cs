using OfiFlow.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tenancy.Commands.RevokeInvitation;
using OfiFlow.Application.Tenancy.Queries.GetInvitations;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

/// <summary>Ver y revocar las invitaciones pendientes (spec 009).</summary>
public class InvitationQueriesAndRevokeTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ListLogger<RevokeInvitationCommandHandler> _revokeLogger = new();

    private async Task<Invitation> SeedAsync(string email, TenantRole role = TenantRole.Technician, DateTime? createdAt = null, DateTime? issuedAt = null)
    {
        var invitation = Invitation.Create(_tenantId, Email.Create(email), role, OpaqueToken.Generate().Hash, _actorId, issuedAt ?? Now);
        if (createdAt is not null)
        {
            invitation.CreatedAt = createdAt.Value; // normalmente lo rellena el interceptor de EF
        }

        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);
        return invitation;
    }

    private GetInvitationsQueryHandler Query(TenantRole actor) =>
        new(_db, new FakeCurrentUser(actor, _actorId, _tenantId), _time);

    private RevokeInvitationCommandHandler Revoke(TenantRole actor) =>
        new(_db, new FakeCurrentUser(actor, _actorId, _tenantId), _time, _revokeLogger);

    // ===== Listar =====

    [Fact]
    public async Task OnlyPendingInvitations_AreListed()
    {
        var pending = await SeedAsync("pendiente@example.com");

        var accepted = await SeedAsync("aceptada@example.com");
        accepted.Accept(Now.AddHours(1));

        var revoked = await SeedAsync("revocada@example.com");
        revoked.Revoke(Now.AddHours(1));

        // Emitida hace 8 días: su plazo de 7 ya pasó.
        var expired = await SeedAsync("caducada@example.com", issuedAt: Now.AddDays(-8));
        await _db.SaveChangesAsync(CancellationToken.None);

        var listed = await Query(TenantRole.Owner).Handle(new GetInvitationsQuery(), CancellationToken.None);

        Assert.Equal([pending.Id], listed.Select(i => i.Id));
        Assert.DoesNotContain(listed, i => i.Id == accepted.Id);
        Assert.DoesNotContain(listed, i => i.Id == revoked.Id);
        Assert.DoesNotContain(listed, i => i.Id == expired.Id);
    }

    [Fact]
    public async Task APendingInvitation_AppearsInTheList_WithItsData_AndWithoutTheToken()
    {
        var invitation = await SeedAsync("Ana@Example.com", TenantRole.Manager);

        var listed = await Query(TenantRole.Owner).Handle(new GetInvitationsQuery(), CancellationToken.None);

        var dto = Assert.Single(listed);
        Assert.Equal(invitation.Id, dto.Id);
        Assert.Equal("Ana@Example.com", dto.Email);
        Assert.Equal("Manager", dto.Role);
        Assert.Equal(invitation.ExpiresAt, dto.ExpiresAt);
    }

    [Fact]
    public void TheListedInvitation_HasNoFieldThatCouldBeUsedToAccept()
    {
        // El token en claro no existe en la base de datos, pero su hash tampoco debe salir hacia la API.
        var properties = typeof(InvitationDto).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(properties, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnExpiredInvitation_IsNotListed()
    {
        await SeedAsync("ana@example.com");
        _time.Advance(Invitation.Lifetime);

        Assert.Empty(await Query(TenantRole.Owner).Handle(new GetInvitationsQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task AnAdmin_DoesNotSeeTheAdminInvitations_ButTheOwnerDoes()
    {
        await SeedAsync("otro-admin@example.com", TenantRole.Admin);
        await SeedAsync("tecnico@example.com", TenantRole.Technician);

        var seenByAdmin = await Query(TenantRole.Admin).Handle(new GetInvitationsQuery(), CancellationToken.None);
        var seenByOwner = await Query(TenantRole.Owner).Handle(new GetInvitationsQuery(), CancellationToken.None);

        Assert.Equal(["Technician"], seenByAdmin.Select(i => i.Role));
        Assert.Equal(2, seenByOwner.Count);
    }

    [Fact]
    public async Task TheList_IsOrderedFromNewestToOldest()
    {
        await SeedAsync("antigua@example.com", createdAt: Now.AddHours(-2));
        await SeedAsync("reciente@example.com", createdAt: Now.AddHours(-1));

        var listed = await Query(TenantRole.Owner).Handle(new GetInvitationsQuery(), CancellationToken.None);

        Assert.Equal(["reciente@example.com", "antigua@example.com"], listed.Select(i => i.Email));
    }

    // ===== Revocar =====

    [Fact]
    public async Task APendingInvitation_CanBeRevoked_AndThenItNoLongerAppears()
    {
        var invitation = await SeedAsync("ana@example.com");

        await Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None);

        var stored = await _db.Invitations.SingleAsync();
        Assert.Equal(Now, stored.RevokedAt);
        Assert.Empty(await Query(TenantRole.Owner).Handle(new GetInvitationsQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task RevokingAnInvitationThatDoesNotExist_IsNotFound()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(InvitationErrors.NotFound, exception.Code);
    }

    [Fact]
    public async Task AnAdmin_CannotRevokeAnAdminInvitation_AndItStaysPending()
    {
        var invitation = await SeedAsync("otro-admin@example.com", TenantRole.Admin);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() =>
            Revoke(TenantRole.Admin).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        Assert.Null((await _db.Invitations.SingleAsync()).RevokedAt);

        // Control positivo: el Owner sí puede.
        await Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None);
        Assert.NotNull((await _db.Invitations.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task RevokingTwice_ThePlainSecondTimeIsInvalid()
    {
        var invitation = await SeedAsync("ana@example.com");
        await Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
    }

    [Fact]
    public async Task AnAcceptedInvitation_CannotBeRevoked()
    {
        var invitation = await SeedAsync("ana@example.com");
        invitation.Accept(Now.AddHours(1));
        await _db.SaveChangesAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Null((await _db.Invitations.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task ARevocation_IsLogged_WithIds_ButNeverTheEmail()
    {
        var invitation = await SeedAsync("ana.secreta@example.com");

        await Revoke(TenantRole.Owner).Handle(new RevokeInvitationCommand(invitation.Id), CancellationToken.None);

        var entry = Assert.Single(_revokeLogger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationRevoked);
        Assert.Contains(invitation.Id.ToString(), entry.Message);
        Assert.Contains(_actorId.ToString(), entry.Message);
        Assert.DoesNotContain("ana.secreta@example.com", entry.Message);
    }
}

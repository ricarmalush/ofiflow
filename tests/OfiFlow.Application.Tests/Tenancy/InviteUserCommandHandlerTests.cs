using OfiFlow.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tenancy.Commands.InviteUser;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

public class InviteUserCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeIdentityService _identity = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ListLogger<InviteUserCommandHandler> _logger = new();

    private InviteUserCommandHandler HandlerFor(TenantRole actorRole) =>
        new(_db, new FakeTenantContext(_tenantId), new FakeCurrentUser(actorRole, _actorId, _tenantId), _identity, _time, _logger);

    private async Task<InvitationCreatedDto> InviteAsync(TenantRole actor, string email, TenantRole role) =>
        await HandlerFor(actor).Handle(new InviteUserCommand(email, role), CancellationToken.None);

    // ===== Camino feliz =====

    [Fact]
    public async Task TheOwnerInvitesATechnician_AndAPendingInvitationIsStored()
    {
        var created = await InviteAsync(TenantRole.Owner, "Ana@Example.com", TenantRole.Technician);

        var stored = await _db.Invitations.SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(_tenantId, stored.TenantId);
        Assert.Equal(TenantRole.Technician, stored.Role);
        Assert.Equal("Ana@Example.com", stored.Email.Value);
        Assert.Equal(_actorId, stored.CreatedByUserId);
        Assert.Equal(Now.AddDays(7), stored.ExpiresAt);
        Assert.Equal(created.ExpiresAt, stored.ExpiresAt);
        Assert.True(stored.IsPendingAt(Now));
    }

    [Fact]
    public async Task TheTokenIsReturnedOnce_AndOnlyItsHashIsStored()
    {
        var created = await InviteAsync(TenantRole.Owner, "ana@example.com", TenantRole.Manager);

        var stored = await _db.Invitations.SingleAsync();

        Assert.True(OpaqueToken.IsWellFormed(created.Token));
        Assert.Equal(OpaqueToken.Hash(created.Token), stored.TokenHash);
        Assert.NotEqual(created.Token, stored.TokenHash);
        // El valor en claro no está en ningún campo de lo guardado.
        Assert.DoesNotContain(created.Token, stored.TokenHash);
        Assert.DoesNotContain(created.Token, stored.NormalizedEmail);
    }

    // ===== Jerarquía (R1, R9) =====

    [Theory]
    [InlineData(TenantRole.Owner, TenantRole.Admin)]
    [InlineData(TenantRole.Owner, TenantRole.Manager)]
    [InlineData(TenantRole.Owner, TenantRole.Technician)]
    [InlineData(TenantRole.Owner, TenantRole.Employee)]
    [InlineData(TenantRole.Admin, TenantRole.Manager)]
    [InlineData(TenantRole.Admin, TenantRole.Technician)]
    [InlineData(TenantRole.Admin, TenantRole.Employee)]
    public async Task WhoCanInvitesWhom_WhenTheHierarchyAllows_ItWorks(TenantRole actor, TenantRole invited)
    {
        var created = await InviteAsync(actor, "ana@example.com", invited);

        Assert.Equal(invited, (await _db.Invitations.SingleAsync(i => i.Id == created.Id)).Role);
    }

    [Theory]
    [InlineData(TenantRole.Owner, TenantRole.Owner)] // el Owner no se concede nunca, ni el Owner a sí mismo
    [InlineData(TenantRole.Admin, TenantRole.Owner)]
    [InlineData(TenantRole.Admin, TenantRole.Admin)] // un Admin no nombra a otro Admin
    public async Task WhenTheHierarchyForbidsIt_ItIsRefused_AndNothingIsCreated(TenantRole actor, TenantRole invited)
    {
        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => InviteAsync(actor, "ana@example.com", invited));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        Assert.False(await _db.Invitations.AnyAsync());
    }

    [Fact]
    public async Task ARefusalByHierarchy_IsLoggedAsAccessDenied_WithoutTheEmail()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => InviteAsync(TenantRole.Admin, "secreta@example.com", TenantRole.Admin));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.AccessDenied);
        Assert.Contains("RoleNotManageable", entry.Message);
        Assert.Contains(_actorId.ToString(), entry.Message);
        Assert.DoesNotContain("secreta@example.com", entry.Message);
    }

    // ===== Email con cuenta (R6) =====

    [Fact]
    public async Task AnEmailThatAlreadyHasAnAccount_CannotBeInvited()
    {
        _identity.AddExistingAccount("ana@example.com");

        var exception = await Assert.ThrowsAsync<IdentityOperationException>(() => InviteAsync(TenantRole.Owner, "ANA@example.com", TenantRole.Employee));

        Assert.Contains(IdentityErrors.EmailAlreadyRegistered, exception.Codes);
        Assert.False(await _db.Invitations.AnyAsync());
    }

    // ===== Una pendiente por email (R5) =====

    [Theory]
    // Variantes de mayúsculas del MISMO email. Hace falta alguna que no sea ya todo mayúsculas: si no, un error que
    // comparara sin normalizar pasaría por casualidad (lo cazó una mutación que sobrevivió).
    [InlineData("Ana@Example.com")]
    [InlineData("ana@example.com")]
    [InlineData("ANA@EXAMPLE.COM")]
    [InlineData("aNa@ExAmPlE.cOm")]
    public async Task ASecondPendingInvitationForTheSameEmail_IsRefused_WhateverTheCase(string secondAttempt)
    {
        await InviteAsync(TenantRole.Owner, "Ana@Example.com", TenantRole.Technician);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => InviteAsync(TenantRole.Owner, secondAttempt, TenantRole.Manager));

        Assert.Equal(InvitationErrors.AlreadyPending, exception.Code);
        Assert.Equal(1, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task AnotherEmail_CanBeInvitedAtTheSameTime()
    {
        // Control positivo del anterior: lo que se rechaza es el email repetido, no una segunda invitación.
        await InviteAsync(TenantRole.Owner, "ana@example.com", TenantRole.Technician);
        await InviteAsync(TenantRole.Owner, "luis@example.com", TenantRole.Technician);

        Assert.Equal(2, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task OnceTheFirstExpires_TheSameEmailCanBeInvitedAgain()
    {
        await InviteAsync(TenantRole.Owner, "ana@example.com", TenantRole.Technician);
        _time.Advance(Invitation.Lifetime);

        await InviteAsync(TenantRole.Owner, "ana@example.com", TenantRole.Technician);

        Assert.Equal(2, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task OnceTheFirstIsRevoked_TheSameEmailCanBeInvitedAgain()
    {
        await InviteAsync(TenantRole.Owner, "ana@example.com", TenantRole.Technician);
        (await _db.Invitations.SingleAsync()).Revoke(Now.AddHours(1));
        await _db.SaveChangesAsync(CancellationToken.None);

        await InviteAsync(TenantRole.Owner, "ana@example.com", TenantRole.Technician);

        Assert.Equal(2, await _db.Invitations.CountAsync());
    }

    // ===== Máximo de pendientes (R5) =====

    private async Task FillPendingAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await InviteAsync(TenantRole.Owner, $"persona{i}@example.com", TenantRole.Employee);
        }
    }

    [Fact]
    public async Task TheLimitOfPendingInvitations_IsEnforced()
    {
        await FillPendingAsync(Invitation.MaxPendingPerTenant);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => InviteAsync(TenantRole.Owner, "una-mas@example.com", TenantRole.Employee));

        Assert.Equal(InvitationErrors.LimitReached, exception.Code);
        Assert.Equal(Invitation.MaxPendingPerTenant, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task OneBelowTheLimit_StillWorks()
    {
        // Control positivo: el rechazo del test anterior se debe al límite y no a otra cosa.
        await FillPendingAsync(Invitation.MaxPendingPerTenant - 1);

        await InviteAsync(TenantRole.Owner, "la-ultima@example.com", TenantRole.Employee);

        Assert.Equal(Invitation.MaxPendingPerTenant, await _db.Invitations.CountAsync());
    }

    [Fact]
    public async Task ExpiredAndRevokedInvitations_DoNotCountTowardsTheLimit()
    {
        await FillPendingAsync(Invitation.MaxPendingPerTenant);
        _time.Advance(Invitation.Lifetime); // las 25 caducan

        await InviteAsync(TenantRole.Owner, "una-mas@example.com", TenantRole.Employee);

        Assert.Equal(Invitation.MaxPendingPerTenant + 1, await _db.Invitations.CountAsync());
    }

    // ===== Eventos de seguridad (ADR-009 R5) =====

    [Fact]
    public async Task ACreatedInvitation_IsLogged_WithIdsAndRole_ButNeverTheEmailOrTheToken()
    {
        var created = await InviteAsync(TenantRole.Owner, "ana.secreta@example.com", TenantRole.Technician);

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationCreated);
        Assert.Contains(created.Id.ToString(), entry.Message);
        Assert.Contains(_tenantId.ToString(), entry.Message);
        Assert.Contains(_actorId.ToString(), entry.Message);
        Assert.Contains("Technician", entry.Message);
        Assert.DoesNotContain("ana.secreta@example.com", entry.Message);
        Assert.DoesNotContain(created.Token, entry.Message);
        Assert.DoesNotContain(OpaqueToken.Hash(created.Token), entry.Message);
    }

    [Fact]
    public async Task ARefusedInvitation_IsNotLoggedAsCreated()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => InviteAsync(TenantRole.Admin, "ana@example.com", TenantRole.Admin));

        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationCreated);
    }
}

using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tenancy.Commands.ChangeUserRole;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

public class ChangeUserRoleCommandHandlerTests : MemberManagementTestBase
{
    private readonly ListLogger<ChangeUserRoleCommandHandler> _logger = new();

    private ChangeUserRoleCommandHandler Handler(TenantRole actor) => new(Db, Actor(actor), _logger);

    private Task ChangeAsync(TenantRole actor, Guid tenantUserId, TenantRole newRole) =>
        Handler(actor).Handle(new ChangeUserRoleCommand(tenantUserId, newRole), CancellationToken.None);

    private async Task<TenantRole> RoleOfAsync(Guid tenantUserId) =>
        (await Db.TenantUsers.SingleAsync(tu => tu.Id == tenantUserId)).Role;

    // ===== Lo que la jerarquía permite =====

    [Theory]
    [InlineData(TenantRole.Owner, TenantRole.Manager, TenantRole.Admin)] // el Owner nombra Admins
    [InlineData(TenantRole.Owner, TenantRole.Admin, TenantRole.Employee)] // y los degrada
    [InlineData(TenantRole.Owner, TenantRole.Employee, TenantRole.Technician)]
    [InlineData(TenantRole.Admin, TenantRole.Employee, TenantRole.Manager)]
    [InlineData(TenantRole.Admin, TenantRole.Manager, TenantRole.Technician)]
    [InlineData(TenantRole.Admin, TenantRole.Technician, TenantRole.Employee)]
    public async Task WhenTheHierarchyAllowsIt_TheRoleChanges(TenantRole actor, TenantRole current, TenantRole newRole)
    {
        await SeedActorAsync(actor);
        var target = await SeedMemberAsync(current);

        await ChangeAsync(actor, target.Id, newRole);

        Assert.Equal(newRole, await RoleOfAsync(target.Id));
    }

    // ===== Lo que NO permite (R9, R10, R12) y que nada cambia =====

    [Theory]
    [InlineData(TenantRole.Admin, TenantRole.Manager, TenantRole.Admin)] // un Admin no asciende a nadie a Admin
    [InlineData(TenantRole.Admin, TenantRole.Employee, TenantRole.Admin)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, TenantRole.Manager)] // ni toca a otro Admin
    [InlineData(TenantRole.Admin, TenantRole.Admin, TenantRole.Employee)]
    [InlineData(TenantRole.Owner, TenantRole.Manager, TenantRole.Owner)] // nadie se convierte en Owner
    [InlineData(TenantRole.Admin, TenantRole.Technician, TenantRole.Owner)]
    [InlineData(TenantRole.Admin, TenantRole.Owner, TenantRole.Manager)] // nadie toca al Owner
    [InlineData(TenantRole.Owner, TenantRole.Owner, TenantRole.Admin)]
    [InlineData(TenantRole.Manager, TenantRole.Employee, TenantRole.Technician)] // quien no gestiona a nadie
    [InlineData(TenantRole.Technician, TenantRole.Employee, TenantRole.Manager)]
    [InlineData(TenantRole.Employee, TenantRole.Technician, TenantRole.Employee)]
    public async Task WhenTheHierarchyForbidsIt_ItIsRefused_AndTheRoleDoesNotChange(TenantRole actor, TenantRole current, TenantRole newRole)
    {
        await SeedActorAsync(actor);
        var target = await SeedMemberAsync(current);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => ChangeAsync(actor, target.Id, newRole));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        Assert.Equal(current, await RoleOfAsync(target.Id));
    }

    [Fact]
    public async Task ARefusalByHierarchy_IsLoggedAsAccessDenied_WithTheReason()
    {
        await SeedActorAsync(TenantRole.Admin);
        var target = await SeedMemberAsync(TenantRole.Manager, "Secreta", "secreta@example.com");

        await Assert.ThrowsAsync<ForbiddenException>(() => ChangeAsync(TenantRole.Admin, target.Id, TenantRole.Admin));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.AccessDenied);
        Assert.Contains("RoleNotManageable", entry.Message);
        Assert.Contains(ActorUserId.ToString(), entry.Message);
        Assert.DoesNotContain("secreta@example.com", entry.Message);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.UserRoleChanged);
    }

    // ===== Nadie se gestiona a sí mismo (R11) =====

    [Theory]
    [InlineData(TenantRole.Admin, TenantRole.Manager)] // degradarse
    [InlineData(TenantRole.Manager, TenantRole.Admin)] // ascenderse
    [InlineData(TenantRole.Owner, TenantRole.Admin)]
    public async Task NobodyCanChangeTheirOwnRole(TenantRole actor, TenantRole newRole)
    {
        var self = await SeedActorAsync(actor);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => ChangeAsync(actor, self.Id, newRole));

        Assert.Equal(TenancyErrors.CannotManageSelf, exception.Code);
        Assert.Equal(actor, await RoleOfAsync(self.Id));
        Assert.Contains("CannotManageSelf", Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.AccessDenied).Message);
    }

    // ===== Buscar al miembro =====

    [Fact]
    public async Task AMemberThatDoesNotExist_IsNotFound()
    {
        await SeedActorAsync(TenantRole.Owner);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => ChangeAsync(TenantRole.Owner, Guid.NewGuid(), TenantRole.Manager));

        Assert.Equal(TenancyErrors.TenantUserNotFound, exception.Code);
    }

    // ===== Efecto, repetición y registro =====

    [Fact]
    public async Task ChangingToTheSameRole_DoesNothing_AndLogsNothing()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Manager);

        await ChangeAsync(TenantRole.Owner, target.Id, TenantRole.Manager);

        Assert.Equal(TenantRole.Manager, await RoleOfAsync(target.Id));
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.UserRoleChanged);
    }

    [Fact]
    public async Task ARoleChange_IsLogged_WithIdsAndBothRoles_ButNeverTheNameOrTheEmail()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Employee, "Ana Secreta", "ana.secreta@example.com");

        await ChangeAsync(TenantRole.Owner, target.Id, TenantRole.Manager);

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.UserRoleChanged);
        Assert.Contains(target.Id.ToString(), entry.Message);
        Assert.Contains(TenantId.ToString(), entry.Message);
        Assert.Contains(ActorUserId.ToString(), entry.Message);
        Assert.Contains("Employee", entry.Message);
        Assert.Contains("Manager", entry.Message);
        Assert.DoesNotContain("Ana Secreta", entry.Message);
        Assert.DoesNotContain("ana.secreta@example.com", entry.Message);
    }

    [Fact]
    public async Task ChangingOneMembersRole_DoesNotTouchTheOthers()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Employee);
        var bystander = await SeedMemberAsync(TenantRole.Technician);

        await ChangeAsync(TenantRole.Owner, target.Id, TenantRole.Manager);

        Assert.Equal(TenantRole.Technician, await RoleOfAsync(bystander.Id));
    }
}

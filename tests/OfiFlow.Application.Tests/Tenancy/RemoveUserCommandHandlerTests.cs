using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tenancy.Commands.RemoveUser;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

public class RemoveUserCommandHandlerTests : MemberManagementTestBase
{
    private readonly FakeIdentityService _identity = new();
    private readonly FakeTokenService _tokens = new();
    private readonly ListLogger<RemoveUserCommandHandler> _logger = new();

    private RemoveUserCommandHandler Handler(TenantRole actor) => new(Db, Actor(actor), _identity, _tokens, _logger);

    private Task RemoveAsync(TenantRole actor, Guid tenantUserId) =>
        Handler(actor).Handle(new RemoveUserCommand(tenantUserId), CancellationToken.None);

    private async Task<Job> SeedJobAsync(Guid assignedTo, Action<Job>? transition = null)
    {
        var job = Job.Create(TenantId, Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        job.AssignTo(assignedTo);
        transition?.Invoke(job);
        Db.Jobs.Add(job);
        await Db.SaveChangesAsync(CancellationToken.None);
        return job;
    }

    private async Task<Guid?> AssignedToAsync(Guid jobId) =>
        (await Db.Jobs.SingleAsync(j => j.Id == jobId)).AssignedTenantUserId;

    // ===== La baja completa (R14) =====

    [Fact]
    public async Task RemovingAMember_DeletesTheMembership_TheProfile_TheCredentialsAndTheSessions()
    {
        await SeedActorAsync(TenantRole.Owner);
        var userId = Guid.NewGuid();
        var target = await SeedMemberAsync(TenantRole.Technician, userId: userId);

        await RemoveAsync(TenantRole.Owner, target.Id);

        Assert.False(await Db.TenantUsers.AnyAsync(tu => tu.Id == target.Id)); // (a) la pertenencia
        Assert.False(await Db.Users.AnyAsync(u => u.Id == userId)); // (d) la ficha, con sus datos personales
        Assert.Equal([userId], _identity.DeletedUsers); // (d) las credenciales
        Assert.Equal([userId], _tokens.UsersWithDeletedTokens); // (b) las sesiones
    }

    [Fact]
    public async Task RemovingAMember_LeavesTheOthersUntouched()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician);
        var bystander = await SeedMemberAsync(TenantRole.Employee, "Se queda");

        await RemoveAsync(TenantRole.Owner, target.Id);

        Assert.True(await Db.TenantUsers.AnyAsync(tu => tu.Id == bystander.Id));
        Assert.True(await Db.Users.AnyAsync(u => u.Id == bystander.UserId));
        Assert.DoesNotContain(bystander.UserId, _identity.DeletedUsers);
        Assert.DoesNotContain(bystander.UserId, _tokens.UsersWithDeletedTokens);
    }

    // ===== Sus trabajos (R14c) =====

    [Fact]
    public async Task TheUnfinishedJobs_AreUnassigned_ButTheFinishedOnesKeepTheirHistory()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician);

        var isNew = await SeedJobAsync(target.Id);
        var inProgress = await SeedJobAsync(target.Id, j => j.Start());
        var completed = await SeedJobAsync(target.Id, j => { j.Start(); j.Complete(); });
        var cancelled = await SeedJobAsync(target.Id, j => j.Cancel());

        await RemoveAsync(TenantRole.Owner, target.Id);

        Assert.Null(await AssignedToAsync(isNew.Id));
        Assert.Null(await AssignedToAsync(inProgress.Id));
        Assert.Equal(target.Id, await AssignedToAsync(completed.Id)); // historial
        Assert.Equal(target.Id, await AssignedToAsync(cancelled.Id)); // historial
    }

    [Fact]
    public async Task AnotherMembersJobs_AreNotUnassigned()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician);
        var other = await SeedMemberAsync(TenantRole.Technician);
        var othersJob = await SeedJobAsync(other.Id);

        await RemoveAsync(TenantRole.Owner, target.Id);

        Assert.Equal(other.Id, await AssignedToAsync(othersJob.Id));
    }

    // ===== Lo que NO se puede quitar (R9, R11, R12) y que nada se toca =====

    private async Task AssertNothingWasDeletedAsync(TenantUser target)
    {
        Assert.True(await Db.TenantUsers.AnyAsync(tu => tu.Id == target.Id));
        Assert.True(await Db.Users.AnyAsync(u => u.Id == target.UserId));
        Assert.Empty(_identity.DeletedUsers);
        Assert.Empty(_tokens.UsersWithDeletedTokens);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.UserRemoved);
    }

    [Theory]
    [InlineData(TenantRole.Admin, TenantRole.Admin)] // un Admin no quita a otro Admin
    [InlineData(TenantRole.Admin, TenantRole.Owner)] // nadie quita al Owner
    [InlineData(TenantRole.Manager, TenantRole.Employee)] // quien no gestiona a nadie
    [InlineData(TenantRole.Technician, TenantRole.Employee)]
    [InlineData(TenantRole.Employee, TenantRole.Technician)]
    public async Task WhenTheHierarchyForbidsIt_ItIsRefused_AndNothingIsDeleted(TenantRole actor, TenantRole targetRole)
    {
        await SeedActorAsync(actor);
        var target = await SeedMemberAsync(targetRole);
        await SeedJobAsync(target.Id);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => RemoveAsync(actor, target.Id));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        await AssertNothingWasDeletedAsync(target);
        Assert.Equal(target.Id, await AssignedToAsync((await Db.Jobs.SingleAsync()).Id)); // su trabajo sigue asignado
    }

    [Fact]
    public async Task TheOwnerCannotBeRemoved_NotEvenByAnotherOwnerRole()
    {
        // Solo hay un Owner por empresa, pero la regla no depende de eso: nadie lo gestiona (R12).
        await SeedActorAsync(TenantRole.Owner);
        var secondOwner = await SeedMemberAsync(TenantRole.Owner);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => RemoveAsync(TenantRole.Owner, secondOwner.Id));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        await AssertNothingWasDeletedAsync(secondOwner);
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    public async Task NobodyCanRemoveThemselves(TenantRole actor)
    {
        var self = await SeedActorAsync(actor);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => RemoveAsync(actor, self.Id));

        Assert.Equal(TenancyErrors.CannotManageSelf, exception.Code);
        await AssertNothingWasDeletedAsync(self);
        Assert.Contains("CannotManageSelf", Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.AccessDenied).Message);
    }

    [Fact]
    public async Task AMemberThatDoesNotExist_IsNotFound()
    {
        await SeedActorAsync(TenantRole.Owner);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => RemoveAsync(TenantRole.Owner, Guid.NewGuid()));

        Assert.Equal(TenancyErrors.TenantUserNotFound, exception.Code);
        Assert.Empty(_identity.DeletedUsers);
    }

    // ===== Datos anómalos y registro =====

    [Fact]
    public async Task AMembershipWithoutProfile_CanStillBeRemoved()
    {
        // Un dato incoherente (pertenencia sin ficha) no debe impedir la baja.
        await SeedActorAsync(TenantRole.Owner);
        var orphan = TenantUser.Create(TenantId, Guid.NewGuid(), TenantRole.Employee);
        Db.TenantUsers.Add(orphan);
        await Db.SaveChangesAsync(CancellationToken.None);

        await RemoveAsync(TenantRole.Owner, orphan.Id);

        Assert.False(await Db.TenantUsers.AnyAsync(tu => tu.Id == orphan.Id));
    }

    [Fact]
    public async Task ARemoval_IsLogged_WithIdsRoleAndCount_ButNeverTheNameOrTheEmail()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician, "Ana Secreta", "ana.secreta@example.com");
        await SeedJobAsync(target.Id);
        await SeedJobAsync(target.Id, j => j.Start());

        await RemoveAsync(TenantRole.Owner, target.Id);

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.UserRemoved);
        Assert.Contains(target.Id.ToString(), entry.Message);
        Assert.Contains(TenantId.ToString(), entry.Message);
        Assert.Contains(ActorUserId.ToString(), entry.Message);
        Assert.Contains("Technician", entry.Message);
        Assert.Contains("2", entry.Message); // trabajos desasignados
        Assert.DoesNotContain("Ana Secreta", entry.Message);
        Assert.DoesNotContain("ana.secreta@example.com", entry.Message);
    }
}

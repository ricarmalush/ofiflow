using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

/// <summary>Preparación común de los tests de miembros (listar, cambiar rol, quitar): una empresa y quien actúa.</summary>
public abstract class MemberManagementTestBase
{
    protected static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    protected readonly Guid TenantId = Guid.NewGuid();
    protected readonly Guid ActorUserId = Guid.NewGuid();
    protected readonly TestDbContext Db = TestDbContextFactory.Create();

    protected FakeCurrentUser Actor(TenantRole role) => new(role, ActorUserId, TenantId);

    /// <summary>Siembra un miembro con su ficha (<c>User</c>) y su pertenencia. Devuelve la pertenencia.</summary>
    protected async Task<TenantUser> SeedMemberAsync(
        TenantRole role, string name = "Persona", string? email = null, Guid? userId = null, DateTime? joinedAt = null)
    {
        var id = userId ?? Guid.NewGuid();
        email ??= $"{id:N}@example.com";

        Db.Users.Add(User.Create(id, name, Email.Create(email)));

        var membership = TenantUser.Create(TenantId, id, role);
        membership.CreatedAt = joinedAt ?? Now;
        Db.TenantUsers.Add(membership);

        await Db.SaveChangesAsync(CancellationToken.None);
        return membership;
    }

    /// <summary>Siembra a quien actúa, con la misma <see cref="ActorUserId"/> que usa <see cref="Actor"/>.</summary>
    protected Task<TenantUser> SeedActorAsync(TenantRole role, string name = "Quien actúa") =>
        SeedMemberAsync(role, name, userId: ActorUserId);
}

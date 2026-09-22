using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// The membership of a User in a Tenant, with the role that applies in THAT tenant only
/// (ADR-004: role lives here, not on AspNetRoles, since a user's role can differ per tenant).
/// Its own aggregate — not a child collection of Tenant — so it can be loaded/queried on its
/// own without pulling in every user of a tenant (ADR-005/006).
/// </summary>
public sealed class TenantUser : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public TenantRole Role { get; private set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private TenantUser()
    {
        // Requerido por EF Core.
    }

    private TenantUser(Guid id, Guid tenantId, Guid userId, TenantRole role)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
    }

    public static TenantUser CreateOwner(Guid tenantId, Guid userId) =>
        new(Guid.NewGuid(), tenantId, userId, TenantRole.Owner);

    public static TenantUser Create(Guid tenantId, Guid userId, TenantRole role) =>
        new(Guid.NewGuid(), tenantId, userId, role);
}

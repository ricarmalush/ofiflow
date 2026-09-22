namespace OfiFlow.Domain.Common;

/// <summary>
/// Implemented by every entity that belongs to a Tenant. Infrastructure configures
/// the EF Core Global Query Filter once, by reflection, over every type implementing
/// this interface — see ADR-002 and ADR-006.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

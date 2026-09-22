namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Not a Domain entity on purpose (ADR-007) — pure session/security bookkeeping.
/// Stores TenantId too (not just UserId): the tenant a session is active for is decided
/// at login and shouldn't silently change on refresh, even once a user can belong to
/// several tenants (NEXT backlog item in spec 002).
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid TenantId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTime ExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    private RefreshToken()
    {
        // Requerido por EF Core.
    }

    public RefreshToken(Guid userId, Guid tenantId, string tokenHash, DateTime expiresAt)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TenantId = tenantId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;

    public void Revoke() => RevokedAt = DateTime.UtcNow;
}

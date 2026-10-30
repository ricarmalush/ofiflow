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

    /// <summary>
    /// La sesión a la que pertenece (spec 015): un inicio de sesión y toda la cadena de renovaciones que sale de él comparten el mismo.
    /// Renovar crea una fila nueva con el SessionId de la anterior; iniciar sesión otra vez crea uno nuevo. El access token lo lleva
    /// como claim «sid» y la API solo lo acepta mientras exista una fila activa de esta sesión.
    /// </summary>
    public Guid SessionId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTime ExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    private RefreshToken()
    {
        // Requerido por EF Core.
    }

    public RefreshToken(Guid userId, Guid tenantId, Guid sessionId, string tokenHash, DateTime expiresAt)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TenantId = tenantId;
        SessionId = sessionId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;

    public void Revoke() => RevokedAt = DateTime.UtcNow;
}

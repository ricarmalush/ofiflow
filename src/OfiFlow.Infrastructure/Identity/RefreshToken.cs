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

    /// <summary>
    /// Cuándo se creó <b>esta fila</b> (spec 016 R7). Cada renovación crea una fila nueva, así que la de la fila <b>activa</b> es la <b>última
    /// actividad</b> de la sesión: se actualiza con cada renovación, es decir, como mucho cada 30 minutos de uso.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Cuándo empezó la <b>sesión</b> (spec 016 R7): el instante del inicio de sesión. La heredan todas las filas de la cadena de renovaciones
    /// (R8): se muestra <i>cómo empezó</i> la sesión, que es lo que su dueño reconoce.
    /// </summary>
    public DateTime SessionStartedAt { get; private set; }

    /// <summary>
    /// El navegador con el que se inició la sesión (spec 016 R7), saneado y recortado; nulo si el cliente no lo envió o la sesión es anterior a
    /// esta spec. Se hereda al renovar (R8): una renovación desde otro navegador no cambia el dispositivo de la sesión.
    /// </summary>
    public string? UserAgent { get; private set; }

    /// <summary>
    /// La IP <b>truncada</b> desde la que se inició la sesión (spec 016 R7 y R9): nunca la completa. Nula si no se conoce o la sesión es
    /// anterior a esta spec. Se hereda al renovar. Vive lo que vive la fila (se borra con ella en la purga).
    /// </summary>
    public string? IpAddress { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    private RefreshToken()
    {
        // Requerido por EF Core.
    }

    /// <param name="createdAt">Cuándo se crea la fila; por defecto, ahora.</param>
    /// <param name="sessionStartedAt">Cuándo empezó la sesión; por defecto, <paramref name="createdAt"/> (un inicio de sesión nuevo). Al renovar se pasa el de la fila anterior.</param>
    public RefreshToken(
        Guid userId,
        Guid tenantId,
        Guid sessionId,
        string tokenHash,
        DateTime expiresAt,
        DateTime? createdAt = null,
        DateTime? sessionStartedAt = null,
        string? userAgent = null,
        string? ipAddress = null)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TenantId = tenantId;
        SessionId = sessionId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt ?? DateTime.UtcNow;
        SessionStartedAt = sessionStartedAt ?? CreatedAt;
        UserAgent = userAgent;
        IpAddress = ipAddress;
    }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;

    public void Revoke() => RevokedAt = DateTime.UtcNow;
}

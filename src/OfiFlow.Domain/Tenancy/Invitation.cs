using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// Una invitación para que una persona entre en la empresa con un rol (ADR-013, spec 009).
/// El token en claro no se guarda nunca: solo su hash (R2). Se usa una sola vez, caduca y se puede
/// revocar mientras esté pendiente (R3). La hora actual entra siempre por parámetro, para que las
/// reglas de caducidad se puedan probar sin esperar.
/// </summary>
public sealed class Invitation : AggregateRoot, ITenantOwned, IAuditable
{
    /// <summary>Cuánto dura una invitación (spec 009 R3).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Máximo de invitaciones pendientes por empresa (spec 009 R5): límite de abuso.</summary>
    public const int MaxPendingPerTenant = 25;

    /// <summary>Fuente única para EF Core: un SHA-256 en base64 son 44 caracteres.</summary>
    public const int TokenHashMaxLength = 64;

    public Guid TenantId { get; private set; }

    public Email Email { get; private set; } = null!;

    /// <summary>El email en mayúsculas, para buscar sin distinguir mayúsculas (como <c>ApplicationUser</c>).</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    public TenantRole Role { get; private set; }

    /// <summary>SHA-256 del token, nunca el token (R2).</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Quién la creó. Solo el Id: no es un dato personal.</summary>
    public Guid CreatedByUserId { get; private set; }

    public DateTime? AcceptedAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private Invitation()
    {
        // Requerido por EF Core.
    }

    private Invitation(Guid id, Guid tenantId, Email email, TenantRole role, string tokenHash, Guid createdByUserId, DateTime expiresAt)
        : base(id)
    {
        TenantId = tenantId;
        Email = email;
        NormalizedEmail = NormalizeEmail(email);
        Role = role;
        TokenHash = tokenHash;
        CreatedByUserId = createdByUserId;
        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// El email en mayúsculas, la forma en que se guarda y se busca (como <c>ApplicationUser</c>). Es pública para que
    /// quien comprueba "¿ya hay una invitación pendiente para este email?" use exactamente la misma regla.
    /// </summary>
    public static string NormalizeEmail(Email email) => email.Value.ToUpperInvariant();

    /// <param name="tokenHash">El hash del token, calculado fuera de Domain. Nunca el token en claro.</param>
    /// <param name="utcNow">La hora actual (UTC); la caducidad es esta hora más <see cref="Lifetime"/>.</param>
    /// <exception cref="DomainException">El rol no se puede conceder: es Owner, o no es un rol (R1).</exception>
    public static Invitation Create(Guid tenantId, Email email, TenantRole role, string tokenHash, Guid createdByUserId, DateTime utcNow)
    {
        if (!ManageableRoles.IsGrantable(role))
        {
            throw new DomainException(TenancyErrors.RoleNotManageable);
        }

        // No es una regla de negocio sino un fallo de programación: un hash vacío dejaría una invitación
        // imposible de buscar por su token.
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenHash.Length, TokenHashMaxLength);

        return new Invitation(Guid.NewGuid(), tenantId, email, role, tokenHash, createdByUserId, utcNow + Lifetime);
    }

    /// <summary>
    /// El estado en el instante dado. Lo definitivo (aceptada, revocada) manda sobre la caducidad: una
    /// invitación aceptada sigue siendo Accepted cuando pasa su fecha.
    /// </summary>
    public InvitationStatus StatusAt(DateTime utcNow)
    {
        if (AcceptedAt is not null)
        {
            return InvitationStatus.Accepted;
        }

        if (RevokedAt is not null)
        {
            return InvitationStatus.Revoked;
        }

        return utcNow >= ExpiresAt ? InvitationStatus.Expired : InvitationStatus.Pending;
    }

    public bool IsPendingAt(DateTime utcNow) => StatusAt(utcNow) == InvitationStatus.Pending;

    /// <summary>Marca la invitación como usada. Solo se puede si está pendiente; el motivo del rechazo no se distingue (R7).</summary>
    /// <exception cref="DomainException">Con <see cref="InvitationErrors.Invalid"/>, sea cual sea el motivo.</exception>
    public void Accept(DateTime utcNow)
    {
        EnsurePending(utcNow);

        AcceptedAt = utcNow;
    }

    /// <summary>Cancela la invitación antes de que se use. Solo se puede si está pendiente.</summary>
    /// <exception cref="DomainException">Con <see cref="InvitationErrors.Invalid"/>.</exception>
    public void Revoke(DateTime utcNow)
    {
        EnsurePending(utcNow);

        RevokedAt = utcNow;
    }

    private void EnsurePending(DateTime utcNow)
    {
        if (!IsPendingAt(utcNow))
        {
            throw new DomainException(InvitationErrors.Invalid);
        }
    }
}

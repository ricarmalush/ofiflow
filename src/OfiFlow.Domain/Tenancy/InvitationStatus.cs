namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// En qué situación está una invitación. No se guarda: se deduce de las fechas (y de la hora actual,
/// para la caducidad), así nunca puede contradecirlas. Ver <see cref="Invitation.StatusAt"/>.
/// </summary>
public enum InvitationStatus
{
    /// <summary>Se puede aceptar o revocar.</summary>
    Pending,

    /// <summary>La persona la aceptó: ya existe su cuenta. Es definitivo.</summary>
    Accepted,

    /// <summary>Alguien de la empresa la canceló antes de que se usara. Es definitivo.</summary>
    Revoked,

    /// <summary>Pasó el plazo sin usarse.</summary>
    Expired
}

namespace OfiFlow.Application.Common.Logging;

/// <summary>
/// Identificadores estables de los eventos de seguridad (ADR-009 R5). Un analista puede
/// filtrar o crear alertas por EventId sin depender del texto del mensaje, que puede cambiar.
/// Ninguno de estos eventos registra contraseñas, tokens, emails ni otros datos personales:
/// solo Ids (UserId, TenantId) y, por el scope de la API, la IP de origen.
/// </summary>
public static class SecurityEventIds
{
    // 1000-1099: autenticación
    public const int LoginFailed = 1001;
    public const int LoginWithoutTenantMembership = 1002;
    public const int RefreshTokenReuseDetected = 1003;
    public const int RefreshTokenRejected = 1004;

    // 1100-1199: abuso
    public const int RateLimitExceeded = 1101;

    // 1200-1299: errores de la aplicación
    public const int UnhandledException = 1201;

    /// <summary>Un usuario autenticado intentó una operación para la que su rol no tiene permiso (ADR-012).</summary>
    public const int AccessDenied = 1301;

    // 1400-1499: usuarios e invitaciones (spec 009). Ninguno registra emails ni tokens: solo Ids, rol y motivo.

    /// <summary>Alguien de la empresa creó una invitación.</summary>
    public const int InvitationCreated = 1401;

    /// <summary>Una invitación se aceptó y nació una cuenta nueva.</summary>
    public const int InvitationAccepted = 1402;

    /// <summary>Se intentó aceptar una invitación que no se podía usar (desconocida, caducada, revocada, usada...).</summary>
    public const int InvitationRejected = 1403;

    /// <summary>Una invitación pendiente se revocó.</summary>
    public const int InvitationRevoked = 1404;

    /// <summary>Se cambió el rol de un miembro de la empresa.</summary>
    public const int UserRoleChanged = 1405;

    /// <summary>Se quitó a un miembro de la empresa (su cuenta y sus datos personales se borran).</summary>
    public const int UserRemoved = 1406;
}

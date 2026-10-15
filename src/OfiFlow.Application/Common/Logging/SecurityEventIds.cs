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

    /// <summary>La cuenta se ha bloqueado temporalmente tras demasiados intentos fallidos (ADR-014, spec 010 R16).</summary>
    public const int AccountLockedOut = 1005;

    /// <summary>Alguien intentó entrar en una cuenta que estaba bloqueada. Una ráfaga de estos sobre el mismo UserId es un ataque en curso.</summary>
    public const int LoginRejectedWhileLocked = 1006;

    // 1100-1199: abuso
    public const int RateLimitExceeded = 1101;

    // 1200-1299: errores de la aplicación
    public const int UnhandledException = 1201;

    /// <summary>Un usuario autenticado intentó una operación para la que su rol no tiene permiso (ADR-012).</summary>
    public const int AccessDenied = 1301;

    /// <summary>
    /// Un token todavía válido de alguien que ya no pertenece a la empresa (se le quitó mientras el token seguía vivo).
    /// Lo detecta la revalidación de la pertenencia en cada petición (ADR-013).
    /// </summary>
    public const int MembershipRevoked = 1302;

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

    // 1500-1599: segundo factor (spec 010). Ninguno registra códigos, secretos ni emails: solo Ids, el motivo y recuentos.

    /// <summary>Alguien pidió el secreto para activar el segundo factor de su cuenta.</summary>
    public const int MfaSetupStarted = 1501;

    /// <summary>Se activó el segundo factor (se cierran las sesiones de la cuenta).</summary>
    public const int MfaEnabled = 1502;

    /// <summary>Se desactivó el segundo factor (se cierran las sesiones de la cuenta).</summary>
    public const int MfaDisabled = 1503;

    /// <summary>Un código del segundo factor fue rechazado (con el motivo: erróneo, ya usado, de recuperación erróneo...).</summary>
    public const int MfaVerificationFailed = 1504;

    /// <summary>Se entró o se confirmó con un código de recuperación (con los que quedan): suele significar que se perdió el móvil.</summary>
    public const int RecoveryCodeUsed = 1505;

    /// <summary>Se generaron códigos de recuperación nuevos; los anteriores dejaron de valer.</summary>
    public const int RecoveryCodesRegenerated = 1506;

    /// <summary>Un gestor reinició el segundo factor de otro miembro (con quién lo hizo y a quién).</summary>
    public const int MfaResetByManager = 1507;
}

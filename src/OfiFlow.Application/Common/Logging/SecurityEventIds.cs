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

    /// <summary>
    /// Alguien de una empresa <b>suspendida</b> intentó entrar, canjear el segundo factor o seguir usando una sesión todavía
    /// válida (spec 011 R15, ADR-016). Lleva la operación y los Ids, nunca datos personales. Una ráfaga sobre la misma empresa
    /// es normal (su equipo reintentando); lo que merece mirarse es una suspensión que no esperaba nadie.
    /// </summary>
    public const int TenantSuspendedAccessDenied = 1303;

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

    // 1600-1699: panel de la plataforma (spec 011). Solo Ids y el motivo: ni emails, ni contraseñas, ni códigos.

    /// <summary>Un operador superó la contraseña y el segundo factor y recibió una sesión de plataforma (15 minutos, sin refresh token).</summary>
    public const int PlatformSessionIssued = 1601;

    /// <summary>
    /// Un inicio de sesión de plataforma fue rechazado, con el <b>motivo real</b> en el registro (credenciales, no es un operador, dado de
    /// baja, sin segundo factor, desafío o código inválido...). Quien lo intenta recibe siempre el mismo 401 (R6).
    /// </summary>
    public const int PlatformLoginRejected = 1602;

    /// <summary>Un operador listó las empresas (con la página y cuántas vio). Es una lectura: no deja fila de auditoría, solo este evento.</summary>
    public const int PlatformTenantsListed = 1603;

    /// <summary>Un operador suspendió una empresa (solo los Ids del operador y de la empresa). Deja también una fila de auditoría.</summary>
    public const int PlatformTenantSuspended = 1604;

    /// <summary>Un operador reactivó una empresa (solo los Ids). Deja también una fila de auditoría.</summary>
    public const int PlatformTenantReactivated = 1605;

    /// <summary>Se creó un operador de la plataforma con el comando del servidor (con su segundo factor ya confirmado).</summary>
    public const int PlatformOperatorCreated = 1606;

    /// <summary>Se dio de baja a un operador de la plataforma con el comando del servidor.</summary>
    public const int PlatformOperatorDisabled = 1607;

    /// <summary>
    /// Un token de plataforma todavía válido fue rechazado porque su cuenta ya no es un operador activo (R10). Un <c>1607</c> seguido de un
    /// <c>1608</c> es la baja haciendo efecto; un <c>1608</c> sin baja previa merece mirarse.
    /// </summary>
    public const int PlatformSessionRevoked = 1608;
}

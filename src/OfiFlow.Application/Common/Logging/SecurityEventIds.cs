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

    /// <summary>
    /// Alguien cerró una sesión (spec 013): se revocó su refresh token. Solo se registra cuando se cierra algo de verdad; un cierre con un
    /// token desconocido, caducado o ya revocado no deja evento. Lleva solo los Ids, nunca el token ni su hash.
    /// </summary>
    public const int SessionClosed = 1007;

    /// <summary>
    /// Una persona pidió cerrar <b>todas</b> sus sesiones (spec 014). A diferencia de <see cref="SessionClosed"/>, se registra en cada
    /// petición, también cuando no había ninguna activa: es una acción explícita y rara de alguien identificado, y el recuento
    /// (cero incluido) es información. Lleva solo el UserId y un número, nunca tokens ni hashes.
    /// </summary>
    public const int AllSessionsClosed = 1008;

    /// <summary>
    /// La API rechazó un access token por su <b>sesión</b> (spec 015): el token no lleva identificador de sesión o su sesión ya no está
    /// activa (se cerró). Es normal que ocurra tras un cierre (una petición en vuelo, una pestaña que no se enteró), por eso es
    /// informativo. Lleva el motivo y, si se conoce, el UserId; nunca el token ni el identificador de sesión.
    /// </summary>
    public const int SessionTokenRejected = 1009;

    /// <summary>
    /// El servicio de purga borró refresh tokens caducados hace más del margen (spec 016). Se registra una vez por ciclo y <b>solo si borró algo</b>:
    /// una ejecución que no encuentra nada no deja evento (sería ruido cada pocas horas). Lleva el recuento y la fecha de corte; nunca tokens,
    /// hashes ni Ids de cuenta (no hay una cuenta: es mantenimiento de la tabla).
    /// </summary>
    public const int RefreshTokensPurged = 1011;

    /// <summary>
    /// El servicio de purga borró tokens de restablecimiento de contraseña caducados hace más del margen (spec 018 R18). Como <see cref="RefreshTokensPurged"/>: una vez por
    /// ciclo y solo si borró algo, con el recuento y la fecha de corte; nunca hashes ni Ids de cuenta (es mantenimiento de la tabla).
    /// </summary>
    public const int PasswordResetTokensPurged = 1012;

    /// <summary>
    /// El servicio que envía el correo de la cola no pudo entregar un mensaje (spec 018 R16). Lleva la <b>categoría</b> del fallo (tiempo agotado, credenciales, rechazo, servidor no
    /// disponible); <b>nunca</b> la dirección, el texto del servidor ni el contenido del mensaje (puede llevar un enlace con un token). Es un <c>Warning</c>: falla un servicio
    /// externo, no el código. Varios seguidos con <c>Authentication</c> o <c>Unavailable</c> son la señal de que el proveedor está mal configurado o caído.
    /// </summary>
    public const int EmailDispatchFailed = 1013;

    /// <summary>
    /// Una persona cerró <b>una sesión concreta</b> suya (spec 016 R17), distinta de «cerrar todas» (<see cref="AllSessionsClosed"/>) y de salir de la
    /// propia (<see cref="SessionClosed"/>). Solo se registra cuando de verdad cerró algo. Lleva el UserId y si era la sesión actual; <b>nunca</b> el Id de la
    /// sesión cerrada, ni tokens ni hashes (una ráfaga de estos del mismo UserId seguida de un inicio de sesión es alguien con una sesión robada echando a su dueño).
    /// </summary>
    public const int SessionClosedByOwner = 1010;

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

    /// <summary>
    /// El correo de una invitación se entregó al servidor de correo (spec 017 R15). Lleva el Id de la invitación, la empresa y quién invitó; <b>nunca</b> la dirección, el token ni el enlace.
    /// Que el servidor lo aceptara no garantiza que llegue a la bandeja de entrada.
    /// </summary>
    public const int InvitationEmailSent = 1407;

    /// <summary>
    /// No se pudo enviar el correo de una invitación y por eso no se creó (spec 017 R15). Lleva la empresa, quién invitó y la <b>categoría</b> del fallo (tiempo agotado, credenciales, rechazo,
    /// servidor no disponible); <b>nunca</b> la dirección ni el texto del servidor de correo, que suele repetirla. Es un <c>Warning</c> y no un <c>Error</c>: falla un servicio externo, no el código.
    /// Varios seguidos con la categoría <c>Authentication</c> o <c>Unavailable</c> son la señal de que el proveedor está mal configurado o caído.
    /// </summary>
    public const int InvitationEmailFailed = 1408;

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

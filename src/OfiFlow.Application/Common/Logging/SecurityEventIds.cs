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
}

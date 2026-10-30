namespace OfiFlow.Application.Identity;

/// <summary>
/// Nombres de los claims propios de OfiFlow en el JWT. TokenService los escribe y TenantContext
/// los lee: una sola definición para que emisor y lector no puedan desincronizarse. El claim
/// de tenant sostiene el aislamiento entre empresas (ADR-002).
/// Cambiar un valor rompe los tokens ya emitidos y a los clientes que lo lean (frontend):
/// es parte del contrato público de la API.
/// </summary>
public static class OfiFlowClaimTypes
{
    public const string TenantId = "tenant_id";

    /// <summary>
    /// El Id de la sesión de un token de empresa (spec 015): un inicio de sesión y todas sus renovaciones comparten el mismo. La API solo
    /// acepta el token mientras esa sesión tenga un refresh token activo, así que cerrar la sesión lo invalida al instante. Es el claim
    /// estándar <c>sid</c> (el mismo valor que <c>JwtRegisteredClaimNames.Sid</c>).
    /// </summary>
    public const string SessionId = "sid";

    /// <summary>El rol del usuario en el tenant activo (nombre de un TenantRole). Lo escribe TokenService y lo lee CurrentUser.</summary>
    public const string Role = "role";

    /// <summary>El «mundo» al que pertenece una sesión (spec 011 R7). Solo lo escribe la sesión de plataforma; una sesión de empresa no lo lleva.</summary>
    public const string Realm = "realm";

    /// <summary>El valor de <see cref="Realm"/> de una sesión de operador de la plataforma.</summary>
    public const string PlatformRealm = "platform";
}

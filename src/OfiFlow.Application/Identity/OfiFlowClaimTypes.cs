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
}

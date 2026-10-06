using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Quién hace la petición, según el token ya validado (ADR-012 R4). Implementado en Infrastructure.
/// Cada dato es nulo si falta o no es válido: el rol, en particular, solo es un valor si es
/// exactamente el nombre de un <see cref="TenantRole"/>. El rol viaja en el token, así que puede
/// estar desfasado hasta que este se renueve (ADR-012, riesgos aceptados).
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    Guid? TenantId { get; }

    TenantRole? Role { get; }
}

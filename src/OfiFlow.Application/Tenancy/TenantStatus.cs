using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Tenancy;

/// <summary>
/// El estado de suspensión de las empresas (spec 011, ADR-016). Es el <b>único</b> sitio que lee la tabla de empresas
/// para saber si una está suspendida: todo el que lo necesita (el login, el canje del segundo factor, la renovación de la
/// sesión, aceptar una invitación y la comprobación de pertenencia de cada petición) pasa por aquí. Un test de
/// arquitectura lo fija, para que una comprobación nueva no pueda olvidarse de la suspensión ni reescribirla distinta.
/// </summary>
public static class TenantStatus
{
    /// <summary>
    /// Las empresas suspendidas, como consulta que se puede componer con otra (por ejemplo, con la pertenencia, para
    /// resolver las dos cosas en una sola ida a la base de datos). La tabla de empresas no tiene filtro de tenant: no
    /// es de una empresa, es la propia empresa.
    /// </summary>
    public static IQueryable<Guid> SuspendedTenantIds(this IApplicationDbContext dbContext) =>
        dbContext.Tenants.Where(tenant => tenant.SuspendedAt != null).Select(tenant => tenant.Id);

    /// <summary>¿Está suspendida esta empresa? Una empresa que no existe no está suspendida (no hay nada que cortar).</summary>
    public static Task<bool> IsSuspendedAsync(this IApplicationDbContext dbContext, Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.SuspendedTenantIds().AnyAsync(id => id == tenantId, cancellationToken);
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Platform.Queries.ListTenants;

public sealed partial class ListTenantsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ILogger<ListTenantsQueryHandler> logger)
    : IRequestHandler<ListTenantsQuery, PagedResult<PlatformTenantDto>>
{
    public async Task<PagedResult<PlatformTenantDto>> Handle(ListTenantsQuery request, CancellationToken cancellationToken)
    {
        // La autorización ya comprobó que es un operador activo; el Id sale del token validado, nunca de la petición. Sin él no hay
        // quien deje rastro: ante la duda, se deniega.
        var operatorUserId = currentUser.UserId ?? throw new ForbiddenException(TenancyErrors.Forbidden);

        // La tabla de empresas no tiene filtro de tenant: no es de una empresa, es la propia empresa (ADR-016).
        var tenants = dbContext.Tenants.AsNoTracking();

        if (request.Suspended is { } suspended)
        {
            tenants = suspended
                ? tenants.Where(tenant => tenant.SuspendedAt != null)
                : tenants.Where(tenant => tenant.SuspendedAt == null);
        }

        var totalCount = await tenants.CountAsync(cancellationToken);

        // Alta descendente y, a igualdad, el Id: un orden TOTAL, para que dos páginas seguidas no repitan ni se salten ninguna.
        var page = await tenants
            .OrderByDescending(tenant => tenant.CreatedAt)
            .ThenBy(tenant => tenant.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(tenant => new PlatformTenantDto(
                tenant.Id,
                tenant.Name,
                tenant.CreatedAt,
                // El ÚNICO sitio donde se cuentan los miembros de empresas ajenas (spec 011, ADR-016): un recuento por empresa de la
                // página. No se proyecta ninguna columna de personas, y por eso se salta el filtro de empresa (lista blanca del test
                // de arquitectura, con este motivo).
                dbContext.TenantUsers.IgnoreQueryFilters().Count(member => member.TenantId == tenant.Id),
                tenant.SuspendedAt))
            .ToListAsync(cancellationToken);

        LogListed(logger, operatorUserId, request.Page, page.Count);

        return new PagedResult<PlatformTenantDto>(page, request.Page, request.PageSize, totalCount);
    }

    [LoggerMessage(EventId = SecurityEventIds.PlatformTenantsListed, Level = LogLevel.Information,
        Message = "Empresas listadas por un operador de la plataforma. UserId {UserId}. Página {Page}. Resultados {Count}")]
    private static partial void LogListed(ILogger logger, Guid userId, int page, int count);
}

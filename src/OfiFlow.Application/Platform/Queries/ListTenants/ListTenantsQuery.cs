using MediatR;
using OfiFlow.Application.Common;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Platform.Queries.ListTenants;

/// <summary>
/// Las empresas que existen, solo con sus metadatos (spec 011 R11): nombre, alta, número de miembros y estado. Paginada, con un
/// orden fijo (alta descendente y, a igualdad, el Id) para que dos páginas seguidas no repitan ni se salten ninguna.
/// </summary>
/// <param name="Suspended">Filtro opcional: <c>true</c> solo las suspendidas, <c>false</c> solo las activas, nulo todas.</param>
[RequiresPlatformOperator]
public sealed record ListTenantsQuery(int Page = 1, int PageSize = ListTenantsQuery.DefaultPageSize, bool? Suspended = null)
    : IRequest<PagedResult<PlatformTenantDto>>
{
    public const int DefaultPageSize = 20;

    /// <summary>El tope de una página (R11): una consulta no puede pedir todas las empresas de golpe.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Una página absurda no es un error de datos sino un desbordamiento esperando: con este tope, (página − 1) × tamaño cabe siempre en un entero.</summary>
    public const int MaxPage = 10_000_000;
}

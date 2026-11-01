using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Queries.ListMySessions;

/// <summary>
/// Mis sesiones activas (spec 016 R10 a R12). Sin parámetros: la cuenta sale del token, así que no hay un Id con el que pedir las sesiones de otra persona.
/// Leer la propia cuenta es <see cref="Permission.AccountRead"/>, que tienen los cinco roles (D7: sin permiso nuevo).
/// </summary>
[RequiresPermission(Permission.AccountRead)]
public sealed record ListMySessionsQuery : IRequest<IReadOnlyList<SessionDto>>;

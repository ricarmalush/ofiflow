using MediatR;
using OfiFlow.Application.Common;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Quotes;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Queries.GetQuotes;

/// <summary>
/// El listado de presupuestos (spec 023 R15, D12): paginado (hasta 100 por página), del más reciente al más antiguo, filtrable por estado y por
/// cliente, y <b>sin las líneas</b>.
/// </summary>
[RequiresPermission(Permission.QuotesRead)]
public sealed record GetQuotesQuery(QuoteStatus? Status, Guid? CustomerId, int Page = 1, int PageSize = GetQuotesQuery.DefaultPageSize)
    : IRequest<PagedResult<QuoteListItemDto>>
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
}

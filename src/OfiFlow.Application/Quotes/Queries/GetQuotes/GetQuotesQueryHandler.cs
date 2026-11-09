using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Quotes.Queries.GetQuotes;

public sealed class GetQuotesQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetQuotesQuery, PagedResult<QuoteListItemDto>>
{
    public async Task<PagedResult<QuoteListItemDto>> Handle(GetQuotesQuery request, CancellationToken cancellationToken)
    {
        // Los filtros globales limitan las dos tablas a la empresa del token (R12).
        var quotes = dbContext.Quotes.AsNoTracking().AsQueryable();

        if (request.Status is { } status)
        {
            quotes = quotes.Where(q => q.Status == status);
        }

        if (request.CustomerId is { } customerId)
        {
            quotes = quotes.Where(q => q.CustomerId == customerId);
        }

        var totalCount = await quotes.CountAsync(cancellationToken);

        var items = await (
                from q in quotes
                join c in dbContext.Customers on q.CustomerId equals c.Id
                orderby q.CreatedAt descending, q.Number descending
                select new QuoteListItemDto(
                    q.Id,
                    q.Number,
                    q.CustomerId,
                    c.Name,
                    q.JobId,
                    q.Title,
                    q.Status.ToString(),
                    q.TotalCents,
                    q.ValidUntil,
                    q.CreatedAt,
                    q.SentAt,
                    q.DecidedAt))
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<QuoteListItemDto>(items, request.Page, request.PageSize, totalCount);
    }
}

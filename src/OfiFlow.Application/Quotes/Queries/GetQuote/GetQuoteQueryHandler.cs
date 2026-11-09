using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Queries.GetQuote;

public sealed class GetQuoteQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetQuoteQuery, QuoteDto>
{
    public async Task<QuoteDto> Handle(GetQuoteQuery request, CancellationToken cancellationToken)
    {
        // El filtro global por empresa: uno de otra empresa es el mismo 404 que uno inexistente (R12).
        var quote = await dbContext.Quotes.AsNoTracking().Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(QuoteErrors.NotFound, request.Id);

        var customerName = await dbContext.Customers
            .Where(c => c.Id == quote.CustomerId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return QuoteDto.From(quote, customerName);
    }
}

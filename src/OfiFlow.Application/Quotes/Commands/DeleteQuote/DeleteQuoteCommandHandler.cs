using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Commands.DeleteQuote;

public sealed class DeleteQuoteCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<DeleteQuoteCommand>
{
    public async Task Handle(DeleteQuoteCommand request, CancellationToken cancellationToken)
    {
        var quote = await dbContext.Quotes.FirstOrDefaultAsync(q => q.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(QuoteErrors.NotFound, request.Id);

        quote.EnsureCanBeDeleted();

        // Las líneas se van con él: la base de datos las borra en cascada.
        dbContext.Quotes.Remove(quote);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Se envió (o se borró) mientras tanto: la fila ya no es la que se leyó (R10).
            throw new BusinessRuleException(QuoteErrors.ChangedMeanwhile);
        }
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Commands.RejectQuote;

public sealed class RejectQuoteCommandHandler(IApplicationDbContext dbContext, TimeProvider timeProvider) : IRequestHandler<RejectQuoteCommand>
{
    public async Task Handle(RejectQuoteCommand request, CancellationToken cancellationToken)
    {
        // El filtro global por empresa: un presupuesto de otra empresa es un 404 (R12).
        var quote = await dbContext.Quotes.FirstOrDefaultAsync(q => q.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(QuoteErrors.NotFound, request.Id);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // La transición la decide el dominio: desde un estado que no es el suyo lanza su código de error.
        quote.Reject(now);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra petición cambió el presupuesto entre que se leyó y se guardó (una edición, otro cambio de estado): gana una (R10).
            throw new BusinessRuleException(QuoteErrors.ChangedMeanwhile);
        }
    }
}

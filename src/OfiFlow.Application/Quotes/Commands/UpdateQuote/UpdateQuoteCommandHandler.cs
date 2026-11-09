using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Commands.UpdateQuote;

public sealed class UpdateQuoteCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<UpdateQuoteCommand>
{
    public async Task Handle(UpdateQuoteCommand request, CancellationToken cancellationToken)
    {
        // El filtro global por empresa: un presupuesto de otra empresa es un 404 (R12).
        var quote = await dbContext.Quotes.Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(QuoteErrors.NotFound, request.Id);

        // La versión que leyó quien edita: si ya no es la actual, alguien lo cambió y no se pisa nada (R10).
        if (!QuoteVersion.Matches(request.Version, quote.RowVersion))
        {
            throw new BusinessRuleException(QuoteErrors.ChangedMeanwhile);
        }

        await QuoteJobLink.EnsureMatchesCustomerAsync(dbContext, request.JobId, quote.CustomerId, cancellationToken);

        // Solo un borrador cambia (R4): si no lo es, Domain lanza quote.not_editable. También recalcula los totales (R5).
        quote.Update(request.JobId, request.Title, request.Notes, request.ValidUntil, request.Items);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException dbg)
        {
            // Otra petición (otra edición, un envío, un borrado) cambió la fila entre que se leyó y se guardó: gana una y esta no deja nada (R10).
            throw new BusinessRuleException(QuoteErrors.ChangedMeanwhile);
        }
    }
}

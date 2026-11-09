using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Commands.CreateQuote;

public sealed class CreateQuoteCommandHandler(IApplicationDbContext dbContext, ITenantContext tenantContext)
    : IRequestHandler<CreateQuoteCommand, Guid>
{
    /// <summary>Intentos de asignar un número antes de rendirse (R6). Con tres, dos o tres creaciones simultáneas se resuelven sin que nadie lo note.</summary>
    public const int MaxNumberAttempts = 3;

    public async Task<Guid> Handle(CreateQuoteCommand request, CancellationToken cancellationToken)
    {
        // Los filtros globales restringen estas búsquedas a la empresa del token: un cliente o un trabajo de otra empresa es el mismo 404 que uno inexistente (R1, R12).
        var customerExists = await dbContext.Customers.AnyAsync(c => c.Id == request.CustomerId, cancellationToken);
        if (!customerExists)
        {
            throw new NotFoundException(CustomerErrors.NotFound, request.CustomerId);
        }

        await QuoteJobLink.EnsureMatchesCustomerAsync(dbContext, request.JobId, request.CustomerId, cancellationToken);

        for (var attempt = 1; attempt <= MaxNumberAttempts; attempt++)
        {
            // El siguiente número de ESTA empresa (el filtro global lo limita). Dos creaciones simultáneas leen el mismo máximo: el índice único
            // (TenantId, Number) rechaza a la segunda y esta la reintenta con el número que ya existe (D5). Sin SQL a mano.
            var lastNumber = await dbContext.Quotes.MaxAsync(q => (int?)q.Number, cancellationToken) ?? 0;

            var quote = Quote.Create(
                tenantContext.TenantId,
                request.CustomerId,
                request.JobId,
                lastNumber + 1,
                request.Title,
                request.Notes,
                request.ValidUntil,
                request.Items);

            dbContext.Quotes.Add(quote);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return quote.Id;
            }
            catch (DbUpdateException)
            {
                // Casi seguro el número que acaba de ocupar otra petición. Se saca el intento fallido del seguimiento para que el siguiente no lo reinserte.
                dbContext.Quotes.Remove(quote);
            }
        }

        throw new BusinessRuleException(QuoteErrors.NumberUnavailable);
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Company;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Queries.GetQuotePdf;

public sealed class GetQuotePdfQueryHandler(IApplicationDbContext dbContext, IQuotePdfRenderer renderer) : IRequestHandler<GetQuotePdfQuery, QuotePdfFile>
{
    public async Task<QuotePdfFile> Handle(GetQuotePdfQuery request, CancellationToken cancellationToken)
    {
        // El filtro global por empresa: el presupuesto de otra empresa es el mismo 404 que uno inexistente (R1, R12).
        var quote = await dbContext.Quotes.AsNoTracking().Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(QuoteErrors.NotFound, request.Id);

        // Sin datos fiscales no hay PDF (R3): se comprueba después del 404 para no decir nada de la empresa a quien pregunta por un presupuesto que no es suyo.
        var profile = await dbContext.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new BusinessRuleException(QuoteErrors.CompanyProfileIncomplete);

        // Del cliente, solo lo que lleva el documento: su nombre y su dirección (R4). Nunca sus notas, su email ni su teléfono.
        var customer = await dbContext.Customers.AsNoTracking()
            .Where(c => c.Id == quote.CustomerId)
            .Select(c => new { c.Name, c.Address })
            .FirstOrDefaultAsync(cancellationToken);

        var model = QuotePdfModelFactory.Create(QuoteDto.From(quote, customer?.Name ?? string.Empty), CompanyProfileDto.From(profile), customer?.Address);

        return new QuotePdfFile(QuoteLabel.FileName(quote.Number), renderer.Render(model));
    }
}

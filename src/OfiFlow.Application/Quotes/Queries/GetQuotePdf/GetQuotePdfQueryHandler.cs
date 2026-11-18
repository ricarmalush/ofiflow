using MediatR;
using OfiFlow.Application.Quotes.Pdf;

namespace OfiFlow.Application.Quotes.Queries.GetQuotePdf;

/// <summary>
/// Descarga el PDF de un presupuesto (spec 025). Desde la spec 026 la lectura y la generación viven en <see cref="QuotePdfBuilder"/>, que comparte con el envío por correo: el documento que recibe
/// el cliente es el mismo que descarga la persona.
/// </summary>
public sealed class GetQuotePdfQueryHandler(QuotePdfBuilder builder) : IRequestHandler<GetQuotePdfQuery, QuotePdfFile>
{
    public async Task<QuotePdfFile> Handle(GetQuotePdfQuery request, CancellationToken cancellationToken) =>
        (await builder.BuildAsync(request.Id, cancellationToken)).File;
}

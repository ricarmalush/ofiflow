using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Company;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes;

/// <summary>
/// Lo que lleva un PDF ya generado, junto con los datos de los que salió. Quien lo pide para mandarlo por correo necesita además el número, el total y los datos de la empresa; <b>sale de las
/// mismas lecturas</b>, no de otras (spec 026 R15). El <c>ToString</c> no imprime nada: son datos fiscales, de un cliente y precios (ADR-009 R5).
/// </summary>
public sealed record QuotePdfDocument(QuotePdfFile File, QuoteDto Quote, CompanyProfileDto Company)
{
    public override string ToString() => nameof(QuotePdfDocument);
}

/// <summary>El presupuesto y lo que hace falta del cliente, leídos y todavía sin generar nada. Mismo <c>ToString</c> sin datos.</summary>
public sealed record QuotePdfSource(Quote Quote, string CustomerName, string? CustomerAddress)
{
    public override string ToString() => nameof(QuotePdfSource);
}

/// <summary>
/// El único sitio que lee un presupuesto, su cliente y el perfil fiscal para generar su PDF (spec 026 R15, D5). Lo usan la descarga (spec 025) y el envío por correo (spec 026): así el documento que
/// recibe el cliente es <b>el mismo</b> que descarga la persona, con el mismo orden de comprobaciones y los mismos errores, y no hay dos copias de esta lectura que se desincronicen.
/// </summary>
/// <remarks>
/// Son dos pasos y no uno porque el envío tiene una comprobación propia <b>entre los dos</b> (sin líneas, <c>400</c> antes que el perfil, R7 de la spec 026): primero <see cref="LoadAsync"/> (el
/// <c>404</c>), luego <see cref="RenderAsync"/> (el perfil, <c>400</c>, y la generación). <see cref="BuildAsync"/> hace los dos seguidos, que es lo que quiere la descarga.
/// </remarks>
public sealed class QuotePdfBuilder(IApplicationDbContext dbContext, IQuotePdfRenderer renderer)
{
    /// <summary>El presupuesto de la empresa con sus líneas. El filtro global por empresa hace que el de otra sea el mismo <c>404</c> que uno inexistente (spec 025 R1).</summary>
    public async Task<QuotePdfSource> LoadAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var quote = await dbContext.Quotes.AsNoTracking().Include(q => q.Items).FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken)
            ?? throw new NotFoundException(QuoteErrors.NotFound, quoteId);

        // Del cliente, solo lo que lleva el documento: su nombre y su dirección (spec 025 R4). Nunca sus notas, su email ni su teléfono.
        var customer = await dbContext.Customers.AsNoTracking()
            .Where(c => c.Id == quote.CustomerId)
            .Select(c => new { c.Name, c.Address })
            .FirstOrDefaultAsync(cancellationToken);

        return new QuotePdfSource(quote, customer?.Name ?? string.Empty, customer?.Address);
    }

    /// <summary>
    /// Sin datos fiscales no hay PDF (spec 025 R3): se comprueba después del <c>404</c> para no decir nada de la empresa a quien pregunta por un presupuesto que no es suyo.
    /// </summary>
    public async Task<QuotePdfDocument> RenderAsync(QuotePdfSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var profile = await dbContext.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new BusinessRuleException(QuoteErrors.CompanyProfileIncomplete);

        var quote = QuoteDto.From(source.Quote, source.CustomerName);
        var company = CompanyProfileDto.From(profile);
        var model = QuotePdfModelFactory.Create(quote, company, source.CustomerAddress);

        return new QuotePdfDocument(new QuotePdfFile(QuoteLabel.FileName(source.Quote.Number), renderer.Render(model)), quote, company);
    }

    public async Task<QuotePdfDocument> BuildAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await RenderAsync(await LoadAsync(quoteId, cancellationToken), cancellationToken);
}

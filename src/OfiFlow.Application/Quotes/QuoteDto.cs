using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes;

/// <summary>Una línea tal y como la ve quien lee el presupuesto: con su neto <b>calculado</b> por el servidor (spec 023 R5).</summary>
public sealed record QuoteItemDto(Guid Id, int Position, string Description, decimal Quantity, long UnitPriceCents, int VatPercent, long NetCents);

/// <summary>El desglose de un tipo de IVA: la base (suma de los netos de ese tipo) y su IVA, calculado una vez sobre esa base (R5).</summary>
public sealed record VatBreakdownDto(int VatPercent, long BaseCents, long VatCents);

/// <summary>
/// Un presupuesto entero. <paramref name="Version"/> es la versión de fila en base64: el cliente la devuelve al modificar y, si alguien lo
/// cambió mientras tanto, la API no pisa nada (R10). Los importes van en <b>céntimos enteros</b>.
/// </summary>
public sealed record QuoteDto(
    Guid Id,
    int Number,
    Guid CustomerId,
    string CustomerName,
    Guid? JobId,
    string Title,
    string? Notes,
    DateOnly? ValidUntil,
    string Status,
    long NetCents,
    long VatCents,
    long TotalCents,
    IReadOnlyList<VatBreakdownDto> VatBreakdown,
    IReadOnlyList<QuoteItemDto> Items,
    DateTime? SentAt,
    DateTime? DecidedAt,
    DateTime CreatedAt,
    string Version)
{
    public static QuoteDto From(Quote quote, string customerName)
    {
        var totals = quote.CalculateTotals();

        return new QuoteDto(
            quote.Id,
            quote.Number,
            quote.CustomerId,
            customerName,
            quote.JobId,
            quote.Title,
            quote.Notes,
            quote.ValidUntil,
            quote.Status.ToString(),
            quote.NetCents,
            quote.VatCents,
            quote.TotalCents,
            totals.Breakdown.Select(b => new VatBreakdownDto(b.VatPercent, b.BaseCents, b.VatCents)).ToList(),
            quote.Items
                .OrderBy(i => i.Position)
                .Select(i => new QuoteItemDto(i.Id, i.Position, i.Description, i.Quantity, i.UnitPriceCents, i.VatPercent, i.NetCents))
                .ToList(),
            quote.SentAt,
            quote.DecidedAt,
            quote.CreatedAt,
            QuoteVersion.Encode(quote.RowVersion));
    }
}

/// <summary>Una fila del listado: cabecera y total, <b>sin las líneas</b> (R15).</summary>
public sealed record QuoteListItemDto(
    Guid Id,
    int Number,
    Guid CustomerId,
    string CustomerName,
    Guid? JobId,
    string Title,
    string Status,
    long TotalCents,
    DateOnly? ValidUntil,
    DateTime CreatedAt,
    DateTime? SentAt,
    DateTime? DecidedAt);

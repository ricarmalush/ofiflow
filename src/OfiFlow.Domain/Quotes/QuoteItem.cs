using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Quotes;

/// <summary>
/// Una línea de un presupuesto. Es parte del agregado <see cref="Quote"/>: no se crea, modifica ni consulta sin él. Lleva su propio
/// <see cref="TenantId"/> (el mismo del presupuesto) para que el filtro global por empresa la cubra también (ADR-002).
/// </summary>
public sealed class QuoteItem : Entity, ITenantOwned
{
    public Guid TenantId { get; private set; }

    public Guid QuoteId { get; private set; }

    /// <summary>El orden en que llegó (desde 0): se conserva (R3).</summary>
    public int Position { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public long UnitPriceCents { get; private set; }

    public int VatPercent { get; private set; }

    private QuoteItem()
    {
        // Requerido por EF Core.
    }

    internal QuoteItem(Guid tenantId, Guid quoteId, int position, QuoteItemInput input)
        : base(Guid.NewGuid())
    {
        TenantId = tenantId;
        QuoteId = quoteId;
        Position = position;
        Description = input.Description.Trim();
        Quantity = input.Quantity;
        UnitPriceCents = input.UnitPriceCents;
        VatPercent = input.VatPercent;
    }

    /// <summary>El neto de esta línea, calculado (nunca guardado): ver <see cref="QuoteCalculator"/>.</summary>
    public long NetCents => QuoteCalculator.LineNetCents(Quantity, UnitPriceCents);
}

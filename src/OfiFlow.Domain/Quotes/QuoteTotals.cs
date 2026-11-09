namespace OfiFlow.Domain.Quotes;

/// <summary>El desglose de un tipo de IVA: la suma de las bases de las líneas con ese tipo y el IVA calculado <b>una vez</b> sobre esa suma.</summary>
public sealed record VatBreakdownLine(int VatPercent, long BaseCents, long VatCents);

/// <summary>El resultado del cálculo de un presupuesto, todo en céntimos: <c>Total = Neto + IVA</c>.</summary>
public sealed record QuoteTotals(long NetCents, long VatCents, long TotalCents, IReadOnlyList<VatBreakdownLine> Breakdown);

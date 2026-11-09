namespace OfiFlow.Domain.Quotes;

/// <summary>
/// <b>La única función del dinero de un presupuesto</b> (spec 023 R5, D4). Con céntimos enteros y <see cref="decimal"/> solo dentro del
/// cálculo:
/// <list type="number">
/// <item>neto de la línea = <c>Quantity × UnitPriceCents</c>, redondeado al céntimo <b>alejándose de cero</b>;</item>
/// <item>base de un tipo de IVA = suma de los netos de las líneas con ese tipo;</item>
/// <item>IVA del tipo = <c>base × tipo / 100</c>, redondeado al céntimo <b>una vez por tipo</b> (no por línea: así lo desglosa una factura,
/// y el presupuesto y la factura futura coincidirán al céntimo);</item>
/// <item>Neto = suma de las bases; IVA = suma de los IVA por tipo; Total = Neto + IVA.</item>
/// </list>
/// El frontend repite esta misma regla solo para una vista previa, con <b>los mismos vectores de prueba</b>; lo que manda siempre es el resultado de
/// esta clase. Las cotas de <see cref="Quote"/> garantizan que ninguna suma desborda un <see cref="long"/>.
/// </summary>
public static class QuoteCalculator
{
    public static long LineNetCents(decimal quantity, long unitPriceCents) =>
        (long)decimal.Round(quantity * unitPriceCents, 0, MidpointRounding.AwayFromZero);

    public static QuoteTotals Calculate(IEnumerable<QuoteItemInput> lines)
    {
        var bases = new SortedDictionary<int, long>();

        foreach (var line in lines)
        {
            bases[line.VatPercent] = bases.GetValueOrDefault(line.VatPercent) + LineNetCents(line.Quantity, line.UnitPriceCents);
        }

        var breakdown = bases
            .Select(pair => new VatBreakdownLine(
                pair.Key,
                pair.Value,
                (long)decimal.Round(pair.Value * pair.Key / 100m, 0, MidpointRounding.AwayFromZero)))
            .ToList();

        var net = breakdown.Sum(b => b.BaseCents);
        var vat = breakdown.Sum(b => b.VatCents);

        return new QuoteTotals(net, vat, net + vat, breakdown);
    }
}

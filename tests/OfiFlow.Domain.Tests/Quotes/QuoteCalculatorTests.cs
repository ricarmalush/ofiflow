using System.Text.Json;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Domain.Tests.Quotes;

/// <summary>
/// El cálculo del dinero de un presupuesto (spec 023 R5, D4) contra una <b>tabla de casos fijos</b> escrita aparte del código
/// (<c>QuoteTotalsVectors.json</c>). El frontend copia ese mismo fichero para su vista previa: si los dos lados dan lo mismo para
/// cada caso, el total que se ve mientras se edita es el que devolverá la API.
/// </summary>
public class QuoteCalculatorTests
{
    private sealed record VectorLine(decimal Quantity, long UnitPriceCents, int VatPercent, int? Repeat);

    private sealed record VectorBreakdown(int VatPercent, long BaseCents, long VatCents);

    private sealed record Vector(string Name, List<VectorLine> Lines, long Net, long Vat, long Total, List<VectorBreakdown> Breakdown);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static List<Vector> LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Quotes", "QuoteTotalsVectors.json");
        return JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), Options)!;
    }

    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in LoadVectors())
        {
            data.Add(vector.Name);
        }

        return data;
    }

    private static IEnumerable<QuoteItemInput> Expand(Vector vector) =>
        vector.Lines.SelectMany(line => Enumerable.Repeat(new QuoteItemInput("línea", line.Quantity, line.UnitPriceCents, line.VatPercent), line.Repeat ?? 1));

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void EveryFixedCase_GivesTheExactBreakdown_NetVatAndTotal(string name)
    {
        var vector = LoadVectors().Single(v => v.Name == name);

        var totals = QuoteCalculator.Calculate(Expand(vector));

        Assert.Equal(vector.Net, totals.NetCents);
        Assert.Equal(vector.Vat, totals.VatCents);
        Assert.Equal(vector.Total, totals.TotalCents);
        Assert.Equal(
            vector.Breakdown.Select(b => (b.VatPercent, b.BaseCents, b.VatCents)),
            totals.Breakdown.Select(b => (b.VatPercent, b.BaseCents, b.VatCents)));
    }

    [Fact]
    public void TheFixedCases_CoverTheRulesTheyExist_ToProtect()
    {
        // Un centinela: si alguien vacía la tabla o borra los casos difíciles, los tests de arriba pasarían en vacío.
        var names = LoadVectors().Select(v => v.Name).ToList();

        Assert.True(names.Count >= 15);
        Assert.Contains(names, n => n.Contains("UNA vez por tipo", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("alejándose de cero", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("cien líneas", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("mezcla de cuatro tipos", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.5, 1, 1)]
    [InlineData(1.5, 1, 2)]
    [InlineData(2.5, 1, 3)]
    [InlineData(0.499, 1, 0)]
    [InlineData(0.001, 500, 1)]
    public void TheLineNet_RoundsToTheCent_MovingAwayFromZero_NotToEven(double quantity, long priceCents, long expected)
    {
        // El redondeo bancario daría 2 para 2,5 y 0 para 0,5: el presupuesto y la factura redondean como se espera en un papel.
        Assert.Equal(expected, QuoteCalculator.LineNetCents((decimal)quantity, priceCents));
    }

    [Fact]
    public void TheVat_IsRoundedOncePerRate_AndTheNetIsTheSumOfTheBases()
    {
        var totals = QuoteCalculator.Calculate(
        [
            new QuoteItemInput("a", 1, 5, 10),
            new QuoteItemInput("b", 1, 5, 10),
            new QuoteItemInput("c", 1, 1000, 21)
        ]);

        Assert.Equal(1010, totals.NetCents);
        Assert.Equal(1 + 210, totals.VatCents);
        Assert.Equal(totals.NetCents + totals.VatCents, totals.TotalCents);
        Assert.Equal(totals.Breakdown.Sum(b => b.BaseCents), totals.NetCents);
        Assert.Equal(totals.Breakdown.Sum(b => b.VatCents), totals.VatCents);
    }

    [Fact]
    public void TheBreakdown_HasOneRowPerRate_InAscendingOrder_WhateverTheOrderOfTheLines()
    {
        var totals = QuoteCalculator.Calculate(
        [
            new QuoteItemInput("a", 1, 100, 21),
            new QuoteItemInput("b", 1, 100, 0),
            new QuoteItemInput("c", 1, 100, 21),
            new QuoteItemInput("d", 1, 100, 10)
        ]);

        Assert.Equal([0, 10, 21], totals.Breakdown.Select(b => b.VatPercent));
    }

    [Fact]
    public void TheOrderOfTheLines_DoesNotChangeTheTotals()
    {
        QuoteItemInput[] lines =
        [
            new("a", 2.5m, 1234, 21),
            new("b", 0.001m, 500, 10),
            new("c", 3, 2500, 4),
            new("d", 1.333m, 300, 21)
        ];

        var forward = QuoteCalculator.Calculate(lines);
        var backward = QuoteCalculator.Calculate(lines.Reverse());

        Assert.Equal(forward.TotalCents, backward.TotalCents);
        Assert.Equal(forward.Breakdown, backward.Breakdown);
    }
}

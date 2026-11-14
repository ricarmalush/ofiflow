using System.Globalization;
using OfiFlow.Infrastructure.Documents;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>
/// El formato español de importes, cantidades, porcentajes y fechas (spec 025 R5 y R6): desde céntimos enteros, con cultura fija y sin depender de la del servidor.
/// </summary>
public class SpanishFormatTests
{
    private static readonly char NonBreakingSpace = (char)0xA0;

    private static string Money(long cents) => MoneyFormat.Format(cents).Replace(NonBreakingSpace, ' ');

    [Theory]
    [InlineData(0, "0,00 €")]
    [InlineData(1, "0,01 €")]
    [InlineData(9, "0,09 €")]
    [InlineData(10, "0,10 €")]
    [InlineData(99, "0,99 €")]
    [InlineData(100, "1,00 €")]
    [InlineData(101, "1,01 €")]
    [InlineData(99_999, "999,99 €")]
    [InlineData(100_000, "1.000,00 €")]
    [InlineData(123_456, "1.234,56 €")]
    [InlineData(100_000_000, "1.000.000,00 €")]
    [InlineData(123_456_789_012, "1.234.567.890,12 €")]
    [InlineData(long.MaxValue, "92.233.720.368.547.758,07 €")]
    public void ThePositiveAmountsAreInSpanishFormat(long cents, string expected)
    {
        Assert.Equal(expected, Money(cents));
    }

    [Theory]
    [InlineData(-1, "-0,01 €")]
    [InlineData(-99, "-0,99 €")]
    [InlineData(-100, "-1,00 €")]
    [InlineData(-123_456, "-1.234,56 €")]
    [InlineData(long.MinValue, "-92.233.720.368.547.758,08 €")]
    public void TheNegativeAmountsKeepTheirSign(long cents, string expected)
    {
        Assert.Equal(expected, Money(cents));
    }

    [Fact]
    public void TheSpaceBeforeTheEuroCannotBreakTheLine()
    {
        var text = MoneyFormat.Format(123_456);

        Assert.Equal(NonBreakingSpace, text[^2]);
        Assert.DoesNotContain(' ', text);
    }

    [Fact]
    public void TheExactMaximumOfAQuoteTotalIsPrintedWithoutLosingACent()
    {
        // 100 líneas de 99 999,999 × 9 999 999 999 céntimos (spec 023): ~1e17 céntimos, lejos de desbordar un long y sin pasar por coma flotante.
        Assert.Equal("1.000.000.000.000.000,00 €", Money(100_000_000_000_000_000));
        Assert.Equal("999.999.999.999.999,99 €", Money(99_999_999_999_999_999));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("10", "10")]
    [InlineData("2.5", "2,5")]
    [InlineData("2.50", "2,5")]
    [InlineData("1.500", "1,5")]
    [InlineData("0.001", "0,001")]
    [InlineData("0.010", "0,01")]
    [InlineData("1000", "1.000")]
    [InlineData("1234.567", "1.234,567")]
    [InlineData("99999.999", "99.999,999")]
    [InlineData("100000", "100.000")]
    public void QuantitiesHaveUpToThreeDecimalsWithoutTrailingZeros(string value, string expected)
    {
        Assert.Equal(expected, SpanishFormat.Quantity(decimal.Parse(value, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void PercentagesUseANonBreakingSpace()
    {
        Assert.Equal("21" + NonBreakingSpace + "%", SpanishFormat.Percent(21));
        Assert.Equal("0" + NonBreakingSpace + "%", SpanishFormat.Percent(0));
    }

    [Theory]
    [InlineData(2026, 11, 10, "10/11/2026")]
    [InlineData(2026, 1, 5, "05/01/2026")]
    [InlineData(2000, 1, 1, "01/01/2000")]
    [InlineData(2099, 12, 31, "31/12/2099")]
    public void DatesAreDayMonthYear(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, SpanishFormat.Date(new DateOnly(year, month, day)));
    }

    // Las mismas entradas dan las mismas salidas con cualquier cultura del hilo: el formato no depende de la configuración regional del servidor.
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("fa-IR")]
    [InlineData("th-TH")]
    public void TheFormatDoesNotChangeWithTheCultureOfTheThread(string culture)
    {
        var original = Thread.CurrentThread.CurrentCulture;
        var originalUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);

            Assert.Equal("1.234,56 €", Money(123_456));
            Assert.Equal("-0,01 €", Money(-1));
            Assert.Equal("1.234,567", SpanishFormat.Quantity(1234.567m));
            Assert.Equal("21" + NonBreakingSpace + "%", SpanishFormat.Percent(21));
            Assert.Equal("10/11/2026", SpanishFormat.Date(new DateOnly(2026, 11, 10)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
            Thread.CurrentThread.CurrentUICulture = originalUi;
        }
    }
}

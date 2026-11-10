using System.Reflection;
using OfiFlow.Application.Quotes.Pdf;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// Lo que el PDF lleva no se imprime donde no debe (spec 025, ADR-009 R5): el <c>ToString</c> de un <c>record</c> lista todas sus propiedades, y aquí hay datos fiscales, nombres, direcciones, texto
/// libre e importes. Cada tipo del espacio de nombres lo sobrescribe, y el número del presupuesto se muestra con su formato.
/// </summary>
public class QuotePdfModelTypesTests
{
    private static readonly QuotePdfIssuer Issuer = new("SECRETO-razón", "SECRETO-nif", "SECRETO-calle", "SECRETO-cp", "SECRETO-ciudad", "SECRETO-prov", "SECRETO-tel", "SECRETO-email");
    private static readonly QuotePdfCustomer Customer = new("SECRETO-cliente", "SECRETO-dirección");
    private static readonly QuotePdfLine Line = new("SECRETO-descripción", 123.456m, 987_654_321, 21, 123_456_789);
    private static readonly QuotePdfVatLine Vat = new(21, 987_654_321, 123_456_789);

    public static TheoryData<object> Instances() =>
    [
        Issuer,
        Customer,
        Line,
        Vat,
        new QuotePdfModel(Issuer, "P-0001", "SECRETO-título", new DateOnly(2026, 11, 10), null, false, Customer, [Line], [Vat], 1, 2, 3, "SECRETO-notas")
    ];

    [Theory]
    [MemberData(nameof(Instances))]
    public void ToStringPrintsOnlyTheTypeName(object instance)
    {
        var text = instance.ToString();

        Assert.Equal(instance.GetType().Name, text);
        Assert.DoesNotContain("SECRETO", text, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRecordOfTheNamespaceOverridesToString()
    {
        // El escaneo: un tipo nuevo del documento que se olvide de sobrescribir ToString haría que un registro imprimiera sus datos.
        var records = typeof(QuotePdfModel).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(QuotePdfModel).Namespace && t.GetMethod("<Clone>$") is not null)
            .ToList();

        Assert.NotEmpty(records);
        foreach (var type in records)
        {
            var toString = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, Type.EmptyTypes);
            Assert.True(toString is not null, $"{type.Name} no sobrescribe ToString: imprimiría sus propiedades.");
        }
    }

    [Theory]
    [InlineData(1, "P-0001", "presupuesto-P-0001.pdf")]
    [InlineData(12, "P-0012", "presupuesto-P-0012.pdf")]
    [InlineData(9999, "P-9999", "presupuesto-P-9999.pdf")]
    [InlineData(12345, "P-12345", "presupuesto-P-12345.pdf")]
    [InlineData(int.MaxValue, "P-2147483647", "presupuesto-P-2147483647.pdf")]
    public void TheLabelAndTheFileNameAreBuiltFromTheNumberAlone(int number, string label, string fileName)
    {
        Assert.Equal(label, QuoteLabel.Format(number));
        Assert.Equal(fileName, QuoteLabel.FileName(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANumberBelowOneIsRejected(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuoteLabel.Format(number));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuoteLabel.FileName(number));
    }

    [Fact]
    public void TheLabelDoesNotDependOnTheCultureOfTheThread()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // Una cultura que usa otros dígitos: «P-0012» tiene que seguir siendo con dígitos latinos.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");

            Assert.Equal("P-0012", QuoteLabel.Format(12));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}

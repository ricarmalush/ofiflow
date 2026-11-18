using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;

namespace OfiFlow.Domain.Tests.Company;

public class SpanishPostalCodeTests
{
    [Theory]
    [InlineData("01001")] // la primera provincia (Álava)
    [InlineData("28001")] // Madrid
    [InlineData("08001")] // Barcelona, con el cero delante
    [InlineData("52001")] // la última (Melilla)
    [InlineData("52999")]
    [InlineData("01999")]
    public void FiveDigitsWithAProvinceFrom01To52_AreValid(string code)
    {
        Assert.True(SpanishPostalCode.IsValid(code));
        Assert.Equal(code, SpanishPostalCode.Create(code).Value);
    }

    [Theory]
    [InlineData("00001")] // provincia 00: no existe
    [InlineData("00000")]
    [InlineData("53001")] // provincia 53: no existe
    [InlineData("99999")]
    [InlineData("2800")] // cuatro cifras
    [InlineData("280011")] // seis cifras
    [InlineData("2800A")] // una letra
    [InlineData("A8001")]
    [InlineData("28 01")] // un espacio dentro
    [InlineData("2-001")]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData("٢٨٠٠١")] // cifras de otro alfabeto (árabe-índicas)
    [InlineData("２８００１")] // cifras de ancho completo
    public void Anything_ElseIsInvalid(string code)
    {
        Assert.False(SpanishPostalCode.IsValid(code));

        var error = Assert.Throws<DomainException>(() => SpanishPostalCode.Create(code));
        Assert.Equal(CompanyErrors.PostalCodeInvalid, error.Code);
    }

    [Fact]
    public void ANull_IsInvalid()
    {
        Assert.False(SpanishPostalCode.IsValid(null));
        Assert.Equal(CompanyErrors.PostalCodeInvalid, Assert.Throws<DomainException>(() => SpanishPostalCode.Create(null)).Code);
    }

    [Fact]
    public void TheSpacesAtTheEnds_DoNotCount_AndAreNotStored()
    {
        Assert.True(SpanishPostalCode.IsValid("  28001 "));
        Assert.Equal("28001", SpanishPostalCode.Create("  28001 ").Value);
    }

    [Fact]
    public void TheError_NeverCarriesTheValue()
    {
        var error = Assert.Throws<DomainException>(() => SpanishPostalCode.Create("99999"));

        Assert.DoesNotContain("99999", error.Message, StringComparison.Ordinal);
        Assert.Empty(error.Arguments);
    }
}

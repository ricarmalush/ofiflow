using Microsoft.Extensions.Options;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Legal;

namespace OfiFlow.Infrastructure.Tests.Legal;

/// <summary>
/// La versión vigente de las condiciones de uso (spec 021 R8, D7, D10). Lo importante: <b>vacía = registro cerrado</b> (fail-closed) y la comparación es exacta, porque la etiqueta es un
/// identificador y no un texto. Qué dicen las condiciones no está en el código: lo redacta el dueño del proyecto.
/// </summary>
public class LegalTermsTests
{
    private static LegalTerms For(string? version) => new(Options.Create(new LegalOptions { TermsVersion = version }));

    // ===== La versión vigente =====

    [Theory]
    [InlineData("2026-11")]
    [InlineData("v1.2")]
    [InlineData("TOS_2027")]
    public void AConfiguredVersion_IsTheCurrentOne(string version)
    {
        var terms = For(version);

        Assert.Equal(version, terms.CurrentVersion);
        Assert.True(terms.IsCurrent(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutAVersion_NothingIsCurrent_TheRegistrationIsClosed(string? configured)
    {
        var terms = For(configured);

        Assert.Null(terms.CurrentVersion);
        Assert.False(terms.IsCurrent(configured));
        Assert.False(terms.IsCurrent("2026-11"));
        Assert.False(terms.IsCurrent(null));
        Assert.False(terms.IsCurrent(string.Empty)); // «vacío acepta vacío» sería abrir el registro sin querer
    }

    [Theory]
    [InlineData("2026-12")]            // otra versión
    [InlineData("2026-11 ")]           // un espacio de más
    [InlineData(" 2026-11")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNotTheCurrentVersion(string? sent)
    {
        Assert.False(For("2026-11").IsCurrent(sent));
    }

    [Fact]
    public void TheComparison_IsCaseSensitive()
    {
        // La etiqueta es un identificador: «TOS-1» y «tos-1» podrían ser dos versiones distintas.
        Assert.True(For("TOS-1").IsCurrent("TOS-1"));
        Assert.False(For("TOS-1").IsCurrent("tos-1"));
    }

    [Fact]
    public void TheVersionIsReadFromTheConfigurationEachTime_NotCached()
    {
        // IOptions<T> no cambia en caliente, pero el servicio no debe guardar su propia copia: lee del objeto de opciones que se le dio.
        var options = new LegalOptions { TermsVersion = "2026-11" };
        var terms = new LegalTerms(Options.Create(options));
        options.TermsVersion = "2027-01";

        Assert.Equal("2027-01", terms.CurrentVersion);
    }

    // ===== La validación al arrancar =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11")]
    [InlineData("v1.2.3")]
    [InlineData("TOS_2027-a")]
    public void AValidConfiguration_HasNoError(string? version)
    {
        Assert.Null(new LegalOptions { TermsVersion = version }.Validate());
    }

    [Theory]
    [InlineData("con espacio")]
    [InlineData("2026 11")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("<script>")]
    [InlineData("año-2026")]
    [InlineData("2026-11\n")]          // el final de línea: \z y no $ (un $ lo dejaría pasar)
    [InlineData("2026-11\r\n")]
    [InlineData("١٢٣")]                // dígitos de otro alfabeto: no son [0-9]
    public void AnInvalidLabel_IsRefused_AndTheMessageNamesTheSetting_NotTheValue(string version)
    {
        var error = new LegalOptions { TermsVersion = version }.Validate();

        Assert.NotNull(error);
        Assert.Contains("Legal:TermsVersion", error);
        // El valor tal cual se envió (con su salto de línea, si lo tiene): el mensaje trae un ejemplo válido («2026-11») que no cuenta como reflejo.
        Assert.DoesNotContain(version, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ALabelAsLongAsTheColumn_IsAccepted_AndOneMoreIsRefused()
    {
        Assert.Null(new LegalOptions { TermsVersion = new string('v', User.TermsVersionMaxLength) }.Validate());
        Assert.NotNull(new LegalOptions { TermsVersion = new string('v', User.TermsVersionMaxLength + 1) }.Validate());
    }

    [Fact]
    public void TheSectionName_IsLegal()
    {
        Assert.Equal("Legal", LegalOptions.SectionName);
    }
}

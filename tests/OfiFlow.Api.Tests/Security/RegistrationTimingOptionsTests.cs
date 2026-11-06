using OfiFlow.Api.Common;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La configuración del suelo de tiempo de «pedir el registro» (spec 021 R2, D2). El mecanismo que espera (<see cref="ResponseTimeFloor"/>) ya lo prueba <c>ForgotPasswordTimingTests</c>; aquí se fija
/// lo propio de este endpoint: su sección, su valor por defecto y su rango. Que el suelo oculte de verdad la diferencia se MIDE con la API real al implementar el bloque 2.
/// </summary>
public class RegistrationTimingOptionsTests
{
    [Fact]
    public void TheSection_IsRegistration_NotTheOneOfPasswordRecovery()
    {
        // Dos endpoints, dos cargas de trabajo, dos ajustes: no se comparte la sección para poder tocar uno sin el otro.
        Assert.Equal("Registration", RegistrationTimingOptions.SectionName);
        Assert.NotEqual(ForgotPasswordTimingOptions.SectionName, RegistrationTimingOptions.SectionName);
    }

    [Fact]
    public void TheDefaultFloor_Is250Ms_AndIsValid()
    {
        var options = new RegistrationTimingOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(250), options.ResponseFloor);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void TheMaximum_IsFiveSeconds_TheSameAsPasswordRecovery()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), RegistrationTimingOptions.MaxFloor);
        Assert.Equal(ForgotPasswordTimingOptions.MaxFloor, RegistrationTimingOptions.MaxFloor);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:05")]
    [InlineData("00:00:00.300")]
    public void AFloorInRange_IsValid(string floor)
    {
        Assert.Null(new RegistrationTimingOptions { ResponseFloor = TimeSpan.Parse(floor) }.Validate());
    }

    [Theory]
    [InlineData("-00:00:00.001")]
    [InlineData("00:00:05.001")]
    [InlineData("01:00:00")]
    public void AFloorOutOfRange_IsRejected_NamingTheSetting_AndNotTheValue(string floor)
    {
        var message = new RegistrationTimingOptions { ResponseFloor = TimeSpan.Parse(floor) }.Validate();

        Assert.NotNull(message);
        Assert.Contains("Registration:ResponseFloor", message);
        Assert.DoesNotContain(floor, message);
    }
}

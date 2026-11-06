using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Legal;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El cableado del registro (spec 021, bloque 1): que la API registra el servicio de pendientes y su purga, la versión de las condiciones, y que la consola de administración no recibe
/// nada de esto. El comportamiento lo prueban los otros ficheros; aquí se protege lo que ellos no ven: quitar una línea del registro no hace fallar ningún test del servicio, que monta
/// su propio contenedor, pero sí dejaría la purga o el caso de uso sin su dependencia.
/// </summary>
public class PendingRegistrationWiringTests
{
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ServiceCollection ApiServices(params (string Key, string? Value)[] extra)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(Config([
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Jwt:Secret", Secret()),
            ("Jwt:PlatformSecret", Secret()),
            ("Mfa:EncryptionKey", Secret()),
            .. extra]));
        return services;
    }

    [Fact]
    public void TheApi_RegistersThePendingRegistrationService_AsItsOwnPurger()
    {
        var services = ApiServices();

        Assert.Contains(services, d => d.ServiceType == typeof(PendingRegistrationService) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(IPendingRegistrationPurger) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void TheApi_RegistersThePendingRegistrationPort_ForTheUseCases()
    {
        // Sin este registro el caso de uso fallaría en la primera petición, no al arrancar.
        Assert.Contains(ApiServices(), d => d.ServiceType == typeof(IPendingRegistrationService) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void ThePurgers_OfTheThreeTables_AreRegisteredTogether_SoTheCycleFindsAllThree()
    {
        // El ciclo de la purga pide los tres: si falta uno, falla el ciclo entero (y se reintenta en silencio en el siguiente).
        var services = ApiServices();

        Assert.Contains(services, d => d.ServiceType == typeof(IRefreshTokenPurger));
        Assert.Contains(services, d => d.ServiceType == typeof(IPasswordResetTokenPurger));
        Assert.Contains(services, d => d.ServiceType == typeof(IPendingRegistrationPurger));
    }

    [Fact]
    public void TheLegalTerms_AreRegistered_AsASingleton()
    {
        Assert.Contains(ApiServices(), d => d.ServiceType == typeof(ILegalTerms) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void WithoutLegalConfiguration_TheRegistrationIsClosed_ByDefault()
    {
        using var provider = ApiServices().BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<ILegalTerms>().CurrentVersion);
    }

    [Fact]
    public void ALegalVersionInTheConfiguration_ReachesTheService()
    {
        using var provider = ApiServices(("Legal:TermsVersion", "2026-11")).BuildServiceProvider();

        Assert.Equal("2026-11", provider.GetRequiredService<ILegalTerms>().CurrentVersion);
        Assert.True(provider.GetRequiredService<ILegalTerms>().IsCurrent("2026-11"));
    }

    [Fact]
    public void AnInvalidLegalVersion_IsRefusedByTheOptionsValidation_AndTheMessageDoesNotEchoTheValue()
    {
        using var provider = ApiServices(("Legal:TermsVersion", "no vale <esto>")).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LegalOptions>>().Value);

        Assert.Contains("TermsVersion", exception.Message);
        Assert.DoesNotContain("no vale <esto>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidLegalVersion_StopsTheStartup_NotTheFirstRegistration()
    {
        // ValidateOnStart hace que el arranque de la aplicación evalúe las opciones: sin él, una etiqueta mala solo se descubriría en la primera petición de registro. IStartupValidator es
        // lo que ejecuta el host al arrancar; se pide aquí directamente y se exige que el fallo sea el de ESTA sección (hay otras opciones validadas al arrancar).
        using var provider = ApiServices(("Legal:TermsVersion", "no vale <esto>")).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Legal", exception.Message);
        Assert.Contains("TermsVersion", exception.Message);
        Assert.DoesNotContain("no vale <esto>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidLegalVersion_DoesNotStopTheStartup()
    {
        // Control del test anterior: con la etiqueta buena, la sección Legal no es la que falla.
        using var provider = ApiServices(("Legal:TermsVersion", "2026-11")).BuildServiceProvider();

        var exception = Record.Exception(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.DoesNotContain("Legal", exception?.Message ?? string.Empty);
    }

    [Fact]
    public void TheAdminConsole_GetsNeitherThePendingRegistrations_NorTheLegalTerms()
    {
        var services = new ServiceCollection();
        services.AddAdminInfrastructure(Config(
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Mfa:EncryptionKey", Secret())));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPendingRegistrationService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPendingRegistrationPurger));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILegalTerms));
    }
}

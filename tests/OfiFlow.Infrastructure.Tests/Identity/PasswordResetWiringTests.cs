using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El cableado de los tokens de restablecimiento (spec 018, bloque 1): que la API los registra y que NO los recibe la consola de administración. El borrado de los
/// caducados lo prueba <see cref="PasswordResetTokenStoreTests"/>; aquí se protege lo que ese no ve: si alguien quitara la línea del registro, la purga dejaría de
/// borrar esta tabla y no fallaría ningún test del servicio (que monta su propio contenedor de servicios).
/// </summary>
public class PasswordResetWiringTests
{
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ServiceCollection ApiServices()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(Config(
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Jwt:Secret", Secret()),
            ("Jwt:PlatformSecret", Secret()),
            ("Mfa:EncryptionKey", Secret())));
        return services;
    }

    [Fact]
    public void TheApi_RegistersThePasswordResetService_AsItsOwnPurger()
    {
        var services = ApiServices();

        Assert.Contains(services, d => d.ServiceType == typeof(PasswordResetService) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(IPasswordResetTokenPurger) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void TheApi_RegistersThePasswordResetPort_ForTheForgotPasswordUseCase()
    {
        // Spec 018: sin este registro el caso de uso fallaría al primer «he olvidado mi contraseña», no al arrancar.
        var services = ApiServices();

        Assert.Contains(services, d => d.ServiceType == typeof(IPasswordResetService) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void ThePurgers_OfBothTables_AreRegisteredTogether_SoTheCycleFindsBoth()
    {
        // El ciclo de la purga pide los dos al mismo tiempo: si falta uno, falla el ciclo entero (y se reintenta en silencio en el siguiente).
        var services = ApiServices();

        Assert.Contains(services, d => d.ServiceType == typeof(IRefreshTokenPurger));
        Assert.Contains(services, d => d.ServiceType == typeof(IPasswordResetTokenPurger));
    }

    [Fact]
    public void TheAdminConsole_DoesNotGetThePurger()
    {
        // La purga es del servicio en segundo plano de la API. La consola solo borra la cuenta (y sus tokens, con IdentityService, sin pasar por la inyección).
        var services = new ServiceCollection();
        services.AddAdminInfrastructure(Config(
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Mfa:EncryptionKey", Secret())));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPasswordResetTokenPurger));
    }
}

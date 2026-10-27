using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La API real no arranca sin la clave propia de la plataforma o con una clave que la confunda con la de las empresas (spec 011 R7,
/// ADR-016). Se prueba con la API entera, como el resto de comprobaciones de arranque, y cada caso lleva su control positivo.
/// </summary>
public class PlatformStartupTests
{
    private static readonly string CompanySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static WebApplicationFactory<Program> Factory(string? platformSecret, string? platformAudience = null, string? companySecret = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Producción: no carga los user-secrets de la máquina, que podrían traer su propia clave y tapar el fallo.
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:Secret", companySecret ?? CompanySecret);
            builder.UseSetting("Jwt:Issuer", "OfiFlow.Api.Tests");
            builder.UseSetting("Jwt:Audience", "OfiFlow.Api.Tests.Client");
            builder.UseSetting("Mfa:EncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("ConnectionStrings:Default", "Server=no-se-usa;Database=no-se-usa");

            if (platformSecret is not null)
            {
                builder.UseSetting("Jwt:PlatformSecret", platformSecret);
            }

            if (platformAudience is not null)
            {
                builder.UseSetting("Jwt:PlatformAudience", platformAudience);
            }
        });
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void WithItsOwnKey_TheApiStarts()
    {
        // Control positivo: sin él, los otros tests podrían fallar por cualquier otra razón de arranque.
        using var factory = Factory(NewKey());

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void WithoutThePlatformKey_TheApiDoesNotStart_AndSaysWhatToDo()
    {
        using var factory = Factory(platformSecret: null);

        var message = Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient()));

        Assert.Contains("Jwt:PlatformSecret", message);
        Assert.Contains("user-secrets", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithABlankPlatformKey_TheApiDoesNotStart(string blank)
    {
        using var factory = Factory(blank);

        Assert.Contains("Jwt:PlatformSecret", Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    [Fact]
    public void WithAKeyThatIsNotBase64_TheApiDoesNotStart()
    {
        using var factory = Factory("esto no es base64 !!");

        Assert.Contains("Base64", Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void WithAKeyOfTheWrongLength_TheApiDoesNotStart(int bytes)
    {
        using var factory = Factory(Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)));

        Assert.Contains($"{bytes} bytes", Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    [Fact]
    public void WithTheSameKeyAsTheCompanies_TheApiDoesNotStart()
    {
        using var factory = Factory(CompanySecret);

        Assert.Contains("distintas", Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    [Fact]
    public void WithTheSameKeyWrittenDifferently_TheApiDoesNotStart()
    {
        // Los mismos 32 bytes con otro texto Base64 (espacios al final tolerados por el decodificador) siguen siendo la misma clave.
        using var factory = Factory(CompanySecret + " ");

        Assert.Contains("distintas", Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    [Fact]
    public void WithTheSameAudienceAsTheCompanies_TheApiDoesNotStart()
    {
        using var factory = Factory(NewKey(), platformAudience: "OfiFlow.Api.Tests.Client");

        Assert.Contains("Jwt:PlatformAudience", Flatten(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    [Fact]
    public void WithADifferentAudience_TheApiStarts()
    {
        using var factory = Factory(NewKey(), platformAudience: "Otra.Audiencia.De.Plataforma");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void TheStartupMessage_NeverReproducesAKey()
    {
        // El mensaje cuenta qué le pasa a la configuración, pero un secreto no se imprime nunca (ni en un log de arranque).
        var short32 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        using var wrongLength = Factory(short32);
        using var duplicated = Factory(CompanySecret);

        var wrongLengthMessage = Flatten(Assert.ThrowsAny<Exception>(() => wrongLength.CreateClient()));
        var duplicatedMessage = Flatten(Assert.ThrowsAny<Exception>(() => duplicated.CreateClient()));

        Assert.DoesNotContain(short32, wrongLengthMessage);
        Assert.DoesNotContain(CompanySecret, duplicatedMessage);
    }

    // El host envuelve la causa en otras excepciones: se busca el mensaje en toda la cadena.
    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}

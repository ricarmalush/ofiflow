using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La API real no arranca sin la clave que cifra los secretos del segundo factor (spec 010 R3, ADR-014), igual que
/// no arranca sin <c>Jwt:Secret</c>. Se prueba con la API entera y no solo con el registro de servicios, para
/// comprobar que el arranque de verdad pasa por ahí.
/// </summary>
public class MfaStartupTests
{
    private static WebApplicationFactory<Program> Factory(string? mfaKey)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Producción: no carga los user-secrets de la máquina, que podrían traer su propia clave y tapar el fallo.
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:Secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Jwt:Issuer", "OfiFlow.Api.Tests");
            builder.UseSetting("Jwt:Audience", "OfiFlow.Api.Tests.Client");
            builder.UseSetting("ConnectionStrings:Default", "Server=no-se-usa;Database=no-se-usa");

            if (mfaKey is not null)
            {
                builder.UseSetting("Mfa:EncryptionKey", mfaKey);
            }
        });
    }

    [Fact]
    public void WithAValidKey_TheApiStarts()
    {
        // Control positivo: sin él, los otros dos tests podrían fallar por cualquier otra razón de arranque.
        using var factory = Factory(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void WithoutTheKey_TheApiDoesNotStart()
    {
        using var factory = Factory(mfaKey: null);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Mfa:EncryptionKey", Flatten(exception));
    }

    [Fact]
    public void WithAKeyOfTheWrongLength_TheApiDoesNotStart()
    {
        using var factory = Factory(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("16 bytes", Flatten(exception));
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

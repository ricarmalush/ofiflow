using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// ADR-017: el frontend web solo puede leer las respuestas de la API desde los orígenes de la configuración. Estos tests
/// hacen lo que hace un navegador: la petición previa (OPTIONS) y la petición real con la cabecera <c>Origin</c>.
/// Se arrancan en Production para que no entre el origen de desarrollo de <c>appsettings.Development.json</c>.
/// </summary>
public class CorsTests
{
    private const string Frontend = "https://app.ejemplo.com";
    private const string Login = "/api/v1/auth/login";

    private static WebApplicationFactory<Program> Factory(string[] origins, Action<IWebHostBuilder>? configure = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Jwt:Secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Jwt:PlatformSecret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Jwt:Issuer", "OfiFlow.Api.Tests");
            builder.UseSetting("Jwt:Audience", "OfiFlow.Api.Tests.Client");
            builder.UseSetting("Mfa:EncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("ConnectionStrings:Default", "Server=no-se-usa;Database=no-se-usa");

            for (var index = 0; index < origins.Length; index++)
            {
                builder.UseSetting($"{FrontendCorsKey}:{index}", origins[index]);
            }

            configure?.Invoke(builder);
        });
    }

    private const string FrontendCorsKey = "Cors:AllowedOrigins";

    private static HttpRequestMessage Preflight(string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, Login);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static string Values(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) ? string.Join(",", values) : string.Empty;

    private static HttpRequestMessage InvalidLogin(string origin)
    {
        // Cuerpo inválido: la validación responde 400 sin tocar la base de datos.
        var request = new HttpRequestMessage(HttpMethod.Post, Login) { Content = JsonContent.Create(new { email = "", password = "" }) };
        request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Preflight_FromAnAllowedOrigin_IsAnsweredWithTheAllowances()
    {
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(Frontend, "POST", "authorization,content-type"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Frontend, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Contains("Authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("600", response.Headers.GetValues("Access-Control-Max-Age").Single());
    }

    [Fact]
    public async Task Responses_NeverAllowBrowserCredentials()
    {
        // La sesión viaja en la cabecera Authorization, no en cookies: permitir credenciales de navegador no hace falta
        // y abriría la puerta a que otra web use las cookies del usuario.
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var preflight = await client.SendAsync(Preflight(Frontend, "POST", "content-type"));
        var actual = await client.SendAsync(InvalidLogin(Frontend));

        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(actual.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_FromAnotherOrigin_GetsNoAllowances()
    {
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://otra-web.example", "POST", "content-type"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_AskingForAVerbTheApiDoesNotUse_DoesNotGetItAllowed()
    {
        // Una lista cerrada también de verbos: nada de AllowAnyMethod. El navegador decide con Access-Control-Allow-Methods.
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(Frontend, "PATCH", "content-type"));

        Assert.DoesNotContain("PATCH", Values(response, "Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task Preflight_AskingForAHeaderTheClientDoesNotSend_DoesNotGetItAllowed()
    {
        // Y de cabeceras: nada de AllowAnyHeader. El navegador decide con Access-Control-Allow-Headers.
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(Frontend, "POST", "x-un-header-raro"));

        Assert.DoesNotContain("x-un-header-raro", Values(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARealRequest_FromAnAllowedOrigin_CanBeReadByTheFrontend()
    {
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(InvalidLogin(Frontend));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Frontend, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        // Sin esta, el navegador escondería Retry-After al JavaScript y el cliente no sabría cuánto esperar tras un 429.
        Assert.Contains("Retry-After", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task ARealRequest_FromAnotherOrigin_CannotBeRead()
    {
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(InvalidLogin("https://otra-web.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task WithoutConfiguredOrigins_NobodyIsAllowed()
    {
        // El comportamiento por defecto es el cerrado: sin lista, ni siquiera el origen de desarrollo habitual.
        using var factory = Factory([]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(InvalidLogin("http://localhost:4200"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ARateLimitedResponse_StillCarriesTheCorsHeaders()
    {
        // Si el 429 no llevara las cabeceras, el navegador lo convertiría en un error de red sin código ni Retry-After.
        using var factory = Factory([Frontend]);
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            (await client.SendAsync(InvalidLogin(Frontend))).Dispose();
        }

        var rejected = await client.SendAsync(InvalidLogin(Frontend));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(Frontend, rejected.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task AnUnhandledError_StillCarriesTheCorsHeaders()
    {
        // El manejador de excepciones borra las cabeceras para escribir el 500: sin esta prueba, un fallo del servidor
        // llegaría al frontend como un error de red opaco en vez de como un 500 con su traceId.
        using var factory = Factory([Frontend], builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ISender, UnhandledExceptionTests.ThrowingSender>()));
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, Login) { Content = JsonContent.Create(new { email = "juan@example.com", password = "x" }) };
        request.Headers.Add("Origin", Frontend);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(Frontend, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("*")] // el comodín abriría la API a cualquier web
    [InlineData("https://app.ejemplo.com/")] // con barra final nunca coincide con lo que envía el navegador
    [InlineData("https://app.ejemplo.com/ruta")] // un origen no lleva ruta
    [InlineData("http://app.ejemplo.com")] // sin cifrar, solo en localhost
    [InlineData("app.ejemplo.com")] // sin esquema
    [InlineData("https://usuario@app.ejemplo.com")] // sin credenciales en la URL
    [InlineData("HTTPS://APP.EJEMPLO.COM")] // el navegador lo envía en minúsculas
    [InlineData("")]
    public void AnInvalidOrigin_StopsTheApiFromStarting(string origin)
    {
        using var factory = Factory([origin]);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(FrontendCorsKey, Flatten(exception));
    }

    [Theory]
    [InlineData("https://app.ejemplo.com")]
    [InlineData("https://app.ejemplo.com:8443")]
    [InlineData("http://localhost:4200")]
    [InlineData("http://127.0.0.1:4200")]
    public void AValidOrigin_LetsTheApiStart(string origin)
    {
        // Control positivo: sin él, los rechazos de arriba podrían deberse a cualquier otro fallo de arranque.
        using var factory = Factory([origin]);

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
            if (current.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", messages);
    }
}

using System.Net;
using System.Net.Http.Json;
using OfiFlow.Api.Tests.Common;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// ADR-009 R4. Factory propia (no compartida con otras clases de test) porque el contador del
/// rate limiting vive en memoria de la instancia de la API.
/// </summary>
public class RateLimitingTests : IClassFixture<OfiFlowApiFactory>
{
    private readonly HttpClient _client;

    public RateLimitingTests(OfiFlowApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_SixthAttemptWithinAMinute_IsRejectedWith429()
    {
        // Cuerpo inválido: la validación responde 400 sin tocar la base de datos, pero cada
        // intento cuenta igualmente para el límite, igual que en un ataque de fuerza bruta.
        var invalidLogin = new { email = "", password = "" };

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var allowed = await _client.PostAsJsonAsync("/api/v1/auth/login", invalidLogin);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var rejected = await _client.PostAsJsonAsync("/api/v1/auth/login", invalidLogin);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task AcceptInvitation_EleventhAttemptWithinAnHour_IsRejectedWith429()
    {
        // La aceptación es anónima y hashea una contraseña (PBKDF2): sin límite sería una vía de agotamiento de CPU (spec 009 D7).
        // Un token con forma incorrecta se rechaza con 400 de validación sin tocar la base de datos, pero cada intento cuenta.
        var malformed = new { token = "no-es-un-token", name = "Ana", password = "una-clave-larga-123" };

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var allowed = await _client.PostAsJsonAsync("/api/v1/auth/accept-invitation", malformed);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var rejected = await _client.PostAsJsonAsync("/api/v1/auth/accept-invitation", malformed);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task MfaVerify_EleventhAttemptWithinAMinute_IsRejectedWith429()
    {
        // Probar códigos de 6 dígitos sin freno sería la vía obvia contra el segundo factor (spec 010). Un cuerpo con un
        // tamaño absurdo se rechaza con 400 de validación sin tocar la base de datos, pero cada intento cuenta.
        var invalid = new { challenge = new string('a', 5000), code = "123456" };

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var allowed = await _client.PostAsJsonAsync("/api/v1/auth/mfa/verify", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var rejected = await _client.PostAsJsonAsync("/api/v1/auth/mfa/verify", invalid);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Theory]
    [InlineData("POST", "/api/v1/account/mfa/setup")]
    [InlineData("POST", "/api/v1/account/mfa/enable")]
    [InlineData("POST", "/api/v1/account/mfa/disable")]
    [InlineData("POST", "/api/v1/account/mfa/recovery-codes")]
    [InlineData("DELETE", "/api/v1/users/00000000-0000-0000-0000-000000000001/mfa")]
    public async Task EveryMfaManagementRoute_IsLimitedToTenPerHourAndIp(string method, string path)
    {
        // El limitador va ANTES de la autenticación (para no gastar CPU en lo que se rechaza), así que sin sesión cada
        // intento da 401 pero cuenta igualmente. Cada ruta tiene su propia IP de prueba, porque el límite es por IP y por política.
        var ip = TestClientIpStartupFilter.NewIp();

        HttpRequestMessage NewRequest()
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { password = "x", code = "123456" }) };
            request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
            return request;
        }

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(NewRequest())).StatusCode);
        }

        var rejected = await _client.SendAsync(NewRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }
}

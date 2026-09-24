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
}

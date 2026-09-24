using System.Net;
using System.Net.Http.Json;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Identity;

namespace OfiFlow.Api.Tests.Security;

/// <summary>ADR-009 R3 y R6, comprobados contra la API real en memoria.</summary>
public class SecurityHeadersAndValidationTests : IClassFixture<OfiFlowApiFactory>
{
    private readonly HttpClient _client;

    public SecurityHeadersAndValidationTests(OfiFlowApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task EveryResponse_IncludesSecurityHeaders()
    {
        // Sin token: 401. Da igual el código, las cabeceras deben estar en todas las respuestas.
        var response = await _client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Register_WithPasswordLongerThanMax_Returns400_Not500()
    {
        var command = new
        {
            companyName = "Fontanería Pérez",
            userName = "Juan Pérez",
            email = "juan@example.com",
            password = new string('a', PasswordRules.MaxLength + 1)
        };

        var response = await _client.PostAsJsonAsync("/api/v1/auth/register", command);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_Returns400_WithoutLeakingInternalTypeNames()
    {
        // Encontrado en la verificación manual: antes acababa en 500 por el GlobalExceptionHandler.
        var content = new StringContent("{\"companyName\": 123", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/auth/register", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("OfiFlow.", body);
        Assert.DoesNotContain("System.Text.Json", body);
    }

    [Fact]
    public async Task ErrorResponses_IncludeTraceIdForCorrelationWithTheLog()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" });

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body!.ContainsKey("traceId"));
    }
}

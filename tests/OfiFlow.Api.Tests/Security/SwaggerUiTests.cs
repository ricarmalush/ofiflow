using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OfiFlow.Api.Common;
using OfiFlow.Api.Tests.Common;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La pantalla de Swagger es una herramienta de desarrollo: existe solo en Development, relaja la
/// CSP únicamente en su ruta y no debe debilitar nada más. Se comprueba contra la API real en memoria.
/// </summary>
public class SwaggerUiTests : IClassFixture<OfiFlowApiFactory>
{
    private readonly OfiFlowApiFactory _factory;

    public SwaggerUiTests(OfiFlowApiFactory factory)
    {
        _factory = factory;
    }

    private static string Csp(HttpResponseMessage response) =>
        response.Headers.GetValues("Content-Security-Policy").Single();

    [Fact]
    public async Task InDevelopment_SwaggerUiIsServed_WithItsOwnCsp_AndTheRestOfTheHeaders()
    {
        var response = await _factory.CreateClient().GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SecurityHeadersMiddleware.SwaggerUiContentSecurityPolicy, Csp(response));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
    }

    [Theory]
    [InlineData("/api/v1/customers")] // protegida: 401, pero con la CSP estricta
    [InlineData("/api/v1/auth/login")] // anónima (GET no existe: 405), también estricta
    [InlineData("/openapi/v1.json")]
    [InlineData("/swaggerfoo")] // empieza igual que /swagger pero no es esa ruta
    [InlineData("/no-existe")]
    public async Task InDevelopment_EveryOtherPath_KeepsTheStrictCsp(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(SecurityHeadersMiddleware.StrictContentSecurityPolicy, Csp(response));
    }

    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger")]
    [InlineData("/openapi/v1.json")]
    public async Task InProduction_NeitherSwaggerNorTheOpenApiDocumentExist_AndTheCspIsStrict(string path)
    {
        using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var response = await production.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(SecurityHeadersMiddleware.StrictContentSecurityPolicy, Csp(response));
    }

    [Fact]
    public void TheSwaggerCsp_DoesNotOpenMoreThanItNeeds()
    {
        // Meta-test: aunque la CSP de Swagger sea más laxa, hay cosas que no puede permitir nunca.
        var policy = SecurityHeadersMiddleware.SwaggerUiContentSecurityPolicy;

        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("default-src 'self'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.DoesNotContain("*", policy);
        Assert.DoesNotContain("http:", policy);
        Assert.DoesNotContain("https:", policy);
    }

    [Fact]
    public async Task TheOpenApiDocument_DeclaresBearer_OnProtectedOperationsOnly()
    {
        var json = await _factory.CreateClient().GetStringAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out var scheme));
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());

        var protectedOperations = 0;
        var anonymousOperations = 0;

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var requiresBearer = operation.Value.TryGetProperty("security", out var security)
                    && security.EnumerateArray().Any(requirement => requirement.TryGetProperty("Bearer", out _));

                if (path.Name.StartsWith("/api/v1/auth", StringComparison.Ordinal))
                {
                    Assert.False(requiresBearer, $"{operation.Name.ToUpperInvariant()} {path.Name} es anónima y no debe marcarse con el candado.");
                    anonymousOperations++;
                }
                else
                {
                    Assert.True(requiresBearer, $"{operation.Name.ToUpperInvariant()} {path.Name} es una ruta protegida y debe pedir el token.");
                    protectedOperations++;
                }
            }
        }

        // Salvaguarda: el test no puede pasar en vacío (19 operaciones autenticadas y 4 anónimas hoy).
        Assert.True(protectedOperations >= 19, $"Se esperaban al menos 19 operaciones protegidas y hay {protectedOperations}.");
        Assert.True(anonymousOperations >= 4, $"Se esperaban al menos 4 operaciones anónimas y hay {anonymousOperations}.");
    }
}

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using OfiFlow.Application.Customers;
using OfiFlow.Application.Jobs;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Spec 007, la versión completa del test obligatorio de la sección 42: dos empresas reales
/// registradas por HTTP, con la API real, el token JWT real y SQL Server real. Cubre el único tramo
/// que el resto de tests no recorre: el claim del token leído por <c>TenantContext</c> y el filtro
/// que de él depende.
/// </summary>
public class TenantIsolationEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>
{
    /// <summary>Todas las rutas de clientes y trabajos que reciben un Id en la ruta.</summary>
    public static TheoryData<string> Routes =>
    [
        "GET customer",
        "PUT customer",
        "DELETE customer",
        "GET job",
        "PUT job",
        "POST job/start",
        "POST job/complete",
        "POST job/cancel",
        "POST job/assign"
    ];

    // --- B no puede alcanzar nada de A ---

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TenantB_WithTenantAsIds_GetsTheSameNotFoundAsForAnIdThatDoesNotExist(string route)
    {
        var foreign = await fixture.B.Client.SendAsync(BuildRequest(route, fixture.CustomerIdOfA, fixture.JobIdOfA));
        var nonexistent = await fixture.B.Client.SendAsync(BuildRequest(route, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);

        var foreignError = await ReadProblemAsync(foreign);
        var nonexistentError = await ReadProblemAsync(nonexistent);

        Assert.Equal(route.Contains("customer") ? "customer.not_found" : "job.not_found", foreignError.Code);
        // Idéntica en todo menos en el traceId: no se revela que el recurso de A existe (ADR-002).
        Assert.Equal(nonexistentError, foreignError);

        await AssertTenantAIsUntouchedAsync();
    }

    [Fact]
    public async Task TenantB_Lists_DoNotIncludeAnythingOfTenantA()
    {
        var customersOfB = await fixture.B.Client.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers");
        var jobsOfB = await fixture.B.Client.GetFromJsonAsync<List<JobDto>>("/api/v1/jobs");

        Assert.DoesNotContain(customersOfB!, c => c.Id == fixture.CustomerIdOfA);
        Assert.DoesNotContain(jobsOfB!, j => j.Id == fixture.JobIdOfA);

        // Control positivo: A sí ve lo suyo, así que "no aparece" no se debe a una preparación fallida.
        var customersOfA = await fixture.A.Client.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers");
        var jobsOfA = await fixture.A.Client.GetFromJsonAsync<List<JobDto>>("/api/v1/jobs");

        Assert.Contains(customersOfA!, c => c.Id == fixture.CustomerIdOfA);
        Assert.Contains(jobsOfA!, j => j.Id == fixture.JobIdOfA);
    }

    [Fact]
    public async Task ATenantIdSentByTheClient_IsIgnored_AndTheDataBelongsToTheTokensTenant()
    {
        // B intenta crear un cliente "dentro" de A enviando el TenantId de A: mass assignment.
        var response = await fixture.B.Client.PostAsJsonAsync("/api/v1/customers",
            new { type = 0, name = "Cliente creado por B", tenantId = fixture.A.TenantId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;

        Assert.Equal(HttpStatusCode.OK, (await fixture.B.Client.GetAsync($"/api/v1/customers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.A.Client.GetAsync($"/api/v1/customers/{id}")).StatusCode);

        var customersOfA = await fixture.A.Client.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers");
        Assert.DoesNotContain(customersOfA!, c => c.Id == id);
    }

    // --- El token es lo que fija la empresa ---

    [Fact]
    public async Task AnAccessTokenWithAnAlteredTenantId_IsRejected()
    {
        var parts = fixture.A.AccessToken.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        Assert.Contains(fixture.A.TenantId.ToString(), payload);

        // Se cambia la empresa dentro del token sin volver a firmarlo: la firma ya no coincide.
        var altered = payload.Replace(fixture.A.TenantId.ToString(), fixture.B.TenantId.ToString(), StringComparison.Ordinal);
        parts[1] = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(altered));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", string.Join('.', parts));
        var response = await fixture.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Control positivo: el token original, sin tocar, sí funciona.
        Assert.Equal(HttpStatusCode.OK, (await fixture.A.Client.GetAsync("/api/v1/customers")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task WithoutAToken_EveryRoute_Returns401(string route)
    {
        var response = await fixture.Anonymous.SendAsync(BuildRequest(route, fixture.CustomerIdOfA, fixture.JobIdOfA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertTenantAIsUntouchedAsync();
    }

    // --- Control positivo: la empresa dueña sí puede usar lo suyo ---
    // Sin él, los "no encontrado" de arriba podrían deberse a una API rota y no al aislamiento.

    [Fact]
    public async Task TenantA_CanUseItsOwnResources()
    {
        var client = fixture.A.Client;

        // Con datos propios y desechables, para no alterar los que comprueban los otros tests.
        var customerId = await TwoTenantsFixture.CreateCustomerAsync(client, "Cliente desechable de A");
        var jobId = await TwoTenantsFixture.CreateJobAsync(client, customerId, "Trabajo desechable de A");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/customers/{customerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/jobs/{jobId}/start", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/jobs/{jobId}/complete", content: null)).StatusCode);

        var update = await client.PutAsJsonAsync($"/api/v1/customers/{customerId}", new { name = "Cliente desechable renombrado" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        // El cliente tiene un trabajo completado: ya no hay trabajos activos, así que se puede borrar.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/customers/{customerId}")).StatusCode);
    }

    // --- Ayudas ---

    private static HttpRequestMessage BuildRequest(string route, Guid customerId, Guid jobId) => route switch
    {
        "GET customer" => new(HttpMethod.Get, $"/api/v1/customers/{customerId}"),
        "PUT customer" => WithJson(HttpMethod.Put, $"/api/v1/customers/{customerId}", new { name = "Cambiado por B" }),
        "DELETE customer" => new(HttpMethod.Delete, $"/api/v1/customers/{customerId}"),
        "GET job" => new(HttpMethod.Get, $"/api/v1/jobs/{jobId}"),
        "PUT job" => WithJson(HttpMethod.Put, $"/api/v1/jobs/{jobId}", new { title = "Cambiado por B", description = "x", priority = 3 }),
        "POST job/start" => new(HttpMethod.Post, $"/api/v1/jobs/{jobId}/start"),
        "POST job/complete" => new(HttpMethod.Post, $"/api/v1/jobs/{jobId}/complete"),
        "POST job/cancel" => new(HttpMethod.Post, $"/api/v1/jobs/{jobId}/cancel"),
        "POST job/assign" => WithJson(HttpMethod.Post, $"/api/v1/jobs/{jobId}/assign", new { tenantUserId = Guid.NewGuid() }),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Ruta desconocida en el test.")
    };

    private static HttpRequestMessage WithJson(HttpMethod method, string url, object body) =>
        new(method, url) { Content = JsonContent.Create(body) };

    /// <summary>Tras los intentos de B, lo de A sigue exactamente como se creó.</summary>
    private async Task AssertTenantAIsUntouchedAsync()
    {
        var customer = await fixture.A.Client.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{fixture.CustomerIdOfA}");
        var job = await fixture.A.Client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{fixture.JobIdOfA}");

        Assert.Equal(TwoTenantsFixture.CustomerNameOfA, customer!.Name);
        Assert.Equal(TwoTenantsFixture.JobTitleOfA, job!.Title);
        Assert.Equal("New", job.Status);
        Assert.Null(job.AssignedTenantUserId);
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())!;

    /// <summary>Los campos de un ProblemDetails que deben coincidir. El traceId se deja fuera a propósito: cambia en cada petición.</summary>
    private sealed record Problem(int? Status, string? Title, string? Detail, string? Code);

    private sealed record Created(Guid Id);
}

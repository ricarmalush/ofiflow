using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Identity;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Dos empresas reales (A y B) registradas por HTTP contra la API con SQL Server real, y un cliente
/// y un trabajo de A. Se prepara una sola vez por clase de test: el registro está limitado a 3 por
/// hora y por IP, así que solo se registran 2 empresas (spec 007).
/// </summary>
public sealed class TwoTenantsFixture : IAsyncLifetime
{
    public const string CustomerNameOfA = "Cliente de A";
    public const string JobTitleOfA = "Trabajo de A";

    public SqlServerApiFactory Factory { get; } = new();

    /// <summary>Un cliente HTTP sin ningún token.</summary>
    public HttpClient Anonymous { get; private set; } = null!;

    public TenantSession A { get; private set; } = null!;

    public TenantSession B { get; private set; } = null!;

    public Guid CustomerIdOfA { get; private set; }

    public Guid JobIdOfA { get; private set; }

    public async Task InitializeAsync()
    {
        await Factory.StartAsync();

        Anonymous = Factory.CreateClient();
        A = await RegisterAndLoginAsync("Fontanería A");
        B = await RegisterAndLoginAsync("Electricidad B");

        CustomerIdOfA = await CreateCustomerAsync(A.Client, CustomerNameOfA);
        JobIdOfA = await CreateJobAsync(A.Client, CustomerIdOfA, JobTitleOfA);
    }

    public async Task DisposeAsync()
    {
        A?.Client.Dispose();
        B?.Client.Dispose();
        Anonymous?.Dispose();
        await Factory.DisposeAsync();
    }

    public static async Task<Guid> CreateCustomerAsync(HttpClient client, string name)
    {
        // El tipo de cliente viaja como número: la API no acepta enumerados como texto (capítulo 4 del manual).
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { type = 0, name });
        return await ReadIdAsync(response);
    }

    public static async Task<Guid> CreateJobAsync(HttpClient client, Guid customerId, string title)
    {
        var response = await client.PostAsJsonAsync("/api/v1/jobs", new { customerId, title, description = "Gotea desde ayer", priority = 1 });
        return await ReadIdAsync(response);
    }

    private async Task<TenantSession> RegisterAndLoginAsync(string companyName)
    {
        // Datos ficticios y generados en cada ejecución: ni credenciales fijas ni datos personales reales.
        var email = $"tenant-{Guid.NewGuid():N}@example.com";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

        var register = await Anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new { companyName, userName = "Usuario de pruebas", email, password });
        var tenantId = (await ReadAsync<RegisterResponse>(register)).TenantId;

        var login = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var tokens = await ReadAsync<AuthResultDto>(login);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return new TenantSession(tenantId, tokens.AccessToken, client);
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response) =>
        (await ReadAsync<CreatedResponse>(response)).Id;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"La preparación del test falló: {(int)response.StatusCode} en {response.RequestMessage?.RequestUri}. " +
                $"WWW-Authenticate: [{response.Headers.WwwAuthenticate}]. " +
                await response.Content.ReadAsStringAsync());
        }

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record RegisterResponse(Guid TenantId);

    private sealed record CreatedResponse(Guid Id);

    /// <summary>Una empresa registrada: su Id, su token de acceso y un cliente HTTP que ya lo lleva puesto.</summary>
    public sealed record TenantSession(Guid TenantId, string AccessToken, HttpClient Client);
}

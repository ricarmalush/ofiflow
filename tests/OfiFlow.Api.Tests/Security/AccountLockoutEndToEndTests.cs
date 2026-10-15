using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El bloqueo de cuenta por HTTP, con la API entera y SQL Server real (spec 010, bloque 3). Lo que el atacante ve: una
/// cuenta bloqueada responde <b>exactamente igual</b> que una contraseña errónea o un email que no existe. Cada
/// petición declara una IP distinta (el límite por IP es otra defensa y aquí no debe intervenir).
/// </summary>
public sealed class AccountLockoutEndToEndTests : IAsyncLifetime
{
    private const string Password = "Password123!";
    private const string Wrong = "incorrecta";

    private readonly CountingPasswordChecker _checker = new();

    private SqlServerApiFactory _factory = null!;
    private WebApplicationFactory<Program> _counted = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory();
        await _factory.StartAsync();

        // La misma API, pero con el verificador que cuenta cada comprobación de contraseña.
        _counted = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IPasswordChecker>(_checker)));
        _client = _counted.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _counted.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private async Task<string> RegisterAsync(string company)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await Post("/api/v1/auth/register", new { companyName = company, userName = "Ana", email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return email;
    }

    private Task<HttpResponseMessage> Post(string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Login(string email, string password) =>
        Post("/api/v1/auth/login", new { email, password });

    private async Task FailLoginsAsync(string email, int count)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, Wrong)).StatusCode);
        }
    }

    /// <summary>Todo lo que el atacante podría usar para distinguir dos respuestas, sin lo que cambia siempre (hora, tamaño).</summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Content-Length"))
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}={string.Join(",", header.Value)}");

        return $"{(int)response.StatusCode}|{string.Join(";", headers)}|{await response.Content.ReadAsStringAsync()}";
    }

    [Fact]
    public async Task AfterFiveWrongPasswords_EvenTheCorrectOneIsRejected()
    {
        var email = await RegisterAsync("Fontanería A");

        Assert.Equal(HttpStatusCode.OK, (await Login(email, Password)).StatusCode); // control: antes de los fallos entra

        await FailLoginsAsync(email, 5);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, Password)).StatusCode);
    }

    [Fact]
    public async Task ALockedAccount_UnknownEmail_AndWrongPassword_ReturnTheSameResponse()
    {
        var wrongEmail = await RegisterAsync("Electricidad B");
        var lockedEmail = await RegisterAsync("Pintura C");
        await FailLoginsAsync(lockedEmail, 5);

        var locked = await FingerprintAsync(await Login(lockedEmail, Password));
        var unknown = await FingerprintAsync(await Login($"{Guid.NewGuid():N}@example.com", Password));
        var wrong = await FingerprintAsync(await Login(wrongEmail, Wrong));

        Assert.StartsWith("401|", locked);
        Assert.Equal(wrong, locked);
        Assert.Equal(wrong, unknown);
    }

    [Fact]
    public async Task OneAccountBeingLocked_DoesNotAffectAnotherCompanysAccount()
    {
        var locked = await RegisterAsync("Gas D");
        var other = await RegisterAsync("Carpintería E");

        await FailLoginsAsync(locked, 5);

        Assert.Equal(HttpStatusCode.OK, (await Login(other, Password)).StatusCode);
    }

    [Fact]
    public async Task ASessionAlreadyOpen_KeepsWorkingWhileTheAccountIsLocked()
    {
        // El bloqueo frena el LOGIN, no cierra sesiones ya abiertas: quien las abrió tenía la contraseña. Es una
        // decisión consciente (spec 010): bloquear a quien ya está dentro lo castigaría a él, no al atacante.
        var email = await RegisterAsync("Climatización F");
        var tokens = await (await Login(email, Password)).Content.ReadFromJsonAsync<JsonElement>();
        await FailLoginsAsync(email, 5);

        using var authorized = _factory.CreateClient();
        authorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/v1/customers")).StatusCode);
    }

    [Fact]
    public async Task FiftySimultaneousWrongLogins_ByHttp_PerformAtMostFiveRealPasswordChecks()
    {
        var email = await RegisterAsync("Cerrajería G");
        var realBefore = _checker.RealVerifications;

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => Login(email, Wrong))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(5, _checker.RealVerifications - realBefore); // R18, ahora con la API entera
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, Password)).StatusCode); // y la cuenta quedó bloqueada
    }

    [Fact]
    public async Task TheSecurityEvents_AreLoggedWithoutTheEmailNorThePassword()
    {
        var email = await RegisterAsync("Albañilería H");
        await FailLoginsAsync(email, 5);
        await Login(email, Password); // bloqueada

        var events = _factory.Logs.Entries.ToList();

        Assert.Contains(events, e => e.EventId == SecurityEventIds.AccountLockedOut);
        Assert.Contains(events, e => e.EventId == SecurityEventIds.LoginRejectedWhileLocked);
        Assert.DoesNotContain(
            events.Where(e => e.EventId is >= 1001 and <= 1006),
            e => e.Message.Contains(email, StringComparison.Ordinal) || e.Message.Contains(Password, StringComparison.Ordinal));
    }
}

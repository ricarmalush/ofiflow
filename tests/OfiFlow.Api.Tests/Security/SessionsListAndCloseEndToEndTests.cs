using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Ver mis sesiones y cerrar una concreta (spec 016, bloque 3) de punta a punta, con la API entera y SQL Server real: el dispositivo y la red que salen en la
/// lista, quién ve qué, que cerrar una deje en pie las demás, que «no existe», «es de otra cuenta» y «ya estaba cerrada» sean <b>la misma respuesta</b>, los cinco
/// roles, el evento, y el cubo de límite que comparte con «cerrar todas». Cada denegación va con su control positivo.
/// </summary>
public class SessionsListAndCloseEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string SessionsPath = "/api/v1/account/sessions";
    private const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string FirefoxLinux = "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0";

    // ===== Ayudas =====

    private static HttpRequestMessage Request(HttpMethod method, string path, string? bearer = null, object? body = null, string? ip = null, string? userAgent = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return request;
    }

    private record Login(string Access, string Refresh, Guid Sid);

    private static Guid SidOf(string accessToken) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.Single(c => c.Type == "sid").Value);

    private static string UserIdOf(string accessToken) => new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;

    /// <summary>Inicia una sesión NUEVA de la cuenta, con el navegador y la IP que se indiquen (o ninguno).</summary>
    private async Task<Login> LoginAsync(MfaFlowFixture.Account account, string? userAgent, string? ip = null)
    {
        var response = await fixture.Http.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/login", body: new { email = account.Email, password = account.Password }, ip: ip, userAgent: userAgent));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await MfaFlowFixture.JsonOf(response);
        var access = body.GetProperty("accessToken").GetString()!;

        return new Login(access, body.GetProperty("refreshToken").GetString()!, SidOf(access));
    }

    private Task<HttpResponseMessage> ListAsync(string? token, string? ip = null) => fixture.Http.SendAsync(Request(HttpMethod.Get, SessionsPath, token, ip: ip));

    private Task<HttpResponseMessage> CloseAsync(string? token, string id, string? ip = null, object? body = null, string query = "") =>
        fixture.Http.SendAsync(Request(HttpMethod.Delete, $"{SessionsPath}/{id}{query}", token, body, ip));

    private async Task<List<JsonElement>> SessionsOfAsync(string token)
    {
        var response = await ListAsync(token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await MfaFlowFixture.JsonOf(response)).EnumerateArray()];
    }

    private List<CapturingLoggerProvider.LogEntry> ClosedByOwnerEventsOf(string userId) =>
        [.. fixture.Logs.Entries.Where(e => e.EventId == SecurityEventIds.SessionClosedByOwner && e.Message.Contains(userId))];

    // ===== Listar =====

    [Fact]
    public async Task TwoBrowsers_AreTwoSessions_WithTheirDevice_AndOnlyTheOneOfTheTokenIsCurrent()
    {
        var account = await fixture.RegisterOwnerAsync();                          // su login deja una sesión sin navegador
        var chrome = await LoginAsync(account, ChromeWindows);
        var firefox = await LoginAsync(account, FirefoxLinux);

        var fromChrome = await SessionsOfAsync(chrome.Access);

        Assert.Equal(3, fromChrome.Count);
        Assert.Equal(["Chrome en Windows", "Dispositivo desconocido", "Firefox en Linux"], fromChrome.Select(s => s.GetProperty("device").GetString()!).Order());
        var current = Assert.Single(fromChrome, s => s.GetProperty("current").GetBoolean());
        Assert.Equal(chrome.Sid.ToString(), current.GetProperty("id").GetString());

        // Visto desde el otro navegador, la actual es la otra.
        var fromFirefox = await SessionsOfAsync(firefox.Access);
        Assert.Equal(firefox.Sid.ToString(), Assert.Single(fromFirefox, s => s.GetProperty("current").GetBoolean()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task ARenewal_DoesNotDuplicateTheSession_NorChangeWhenItStarted_ButMovesItsLastActivity()
    {
        var account = await fixture.RegisterOwnerAsync();
        var login = await LoginAsync(account, ChromeWindows);
        var before = (await SessionsOfAsync(login.Access)).Single(s => s.GetProperty("id").GetString() == login.Sid.ToString());
        await Task.Delay(50);

        var renewed = await fixture.Http.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/refresh", body: new { refreshToken = login.Refresh }, userAgent: "Safari/17 (iPhone)"));
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        var after = await SessionsOfAsync(login.Access);
        var session = Assert.Single(after, s => s.GetProperty("id").GetString() == login.Sid.ToString()); // sigue siendo UNA
        Assert.Equal(before.GetProperty("startedAt").GetDateTime(), session.GetProperty("startedAt").GetDateTime());
        Assert.True(session.GetProperty("lastActiveAt").GetDateTime() > before.GetProperty("lastActiveAt").GetDateTime());
        Assert.Equal("Chrome en Windows", session.GetProperty("device").GetString());              // la renovación desde otro navegador no lo cambia
    }

    [Fact]
    public async Task TheNetwork_IsTheTruncatedOne_NeverTheFullAddress()
    {
        const string fullIp = "198.51.100.231";
        var account = await fixture.RegisterOwnerAsync();
        var login = await LoginAsync(account, ChromeWindows, ip: fullIp);

        var response = await ListAsync(login.Access);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("198.51.100.0", text);
        Assert.DoesNotContain(fullIp, text);
    }

    [Fact]
    public async Task EachAccount_SeesOnlyItsOwnSessions()
    {
        var a = await fixture.RegisterOwnerAsync($"Empresa A {Guid.NewGuid():N}");
        var b = await fixture.RegisterOwnerAsync($"Empresa B {Guid.NewGuid():N}");
        var aChrome = await LoginAsync(a, ChromeWindows);
        var bFirefox = await LoginAsync(b, FirefoxLinux);

        var seenByA = (await SessionsOfAsync(aChrome.Access)).Select(s => s.GetProperty("id").GetString()).ToList();
        var seenByB = (await SessionsOfAsync(bFirefox.Access)).Select(s => s.GetProperty("id").GetString()).ToList();

        Assert.Contains(aChrome.Sid.ToString(), seenByA);
        Assert.DoesNotContain(bFirefox.Sid.ToString(), seenByA);
        Assert.Contains(bFirefox.Sid.ToString(), seenByB);
        Assert.DoesNotContain(aChrome.Sid.ToString(), seenByB);
    }

    [Fact]
    public async Task AClosedSession_DisappearsFromTheList()
    {
        var account = await fixture.RegisterOwnerAsync();
        var keep = await LoginAsync(account, ChromeWindows);
        var gone = await LoginAsync(account, FirefoxLinux);
        Assert.Contains(await SessionsOfAsync(keep.Access), s => s.GetProperty("id").GetString() == gone.Sid.ToString()); // control: antes estaba

        await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", new { refreshToken = gone.Refresh });

        Assert.DoesNotContain(await SessionsOfAsync(keep.Access), s => s.GetProperty("id").GetString() == gone.Sid.ToString());
    }

    [Fact]
    public async Task TheAnswer_IsNeverCached_HasExactlyTheSixFields_AndNothingSensitive()
    {
        const string fullIp = "203.0.113.200";
        var account = await fixture.RegisterOwnerAsync();
        var login = await LoginAsync(account, ChromeWindows, ip: fullIp);

        var response = await ListAsync(login.Access);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        foreach (var session in JsonDocument.Parse(text).RootElement.EnumerateArray())
        {
            Assert.Equal(
                ["approximateIp", "current", "device", "id", "lastActiveAt", "startedAt"],
                session.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }

        foreach (var forbidden in new[] { login.Refresh, login.Access, "Mozilla", "AppleWebKit", "refreshToken", "tokenHash", "hash", fullIp, account.Email })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ABrowserTextWithHtml_ReachesTheScreenAsAFixedLabel_NeverAsTheClientsText()
    {
        var account = await fixture.RegisterOwnerAsync();
        var login = await LoginAsync(account, "<script>alert('x')</script> Windows Chrome/120");

        var text = await (await ListAsync(login.Access)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", text);
        Assert.Contains("Chrome en Windows", text);
    }

    [Fact]
    public async Task ASessionWithoutABrowserHeader_IsAnUnknownDevice_NotAnError()
    {
        var account = await fixture.RegisterOwnerAsync();
        var login = await LoginAsync(account, userAgent: null);

        var session = (await SessionsOfAsync(login.Access)).Single(s => s.GetProperty("id").GetString() == login.Sid.ToString());

        Assert.Equal("Dispositivo desconocido", session.GetProperty("device").GetString());
    }

    // ===== Quién puede llamar =====

    [Fact]
    public async Task WithoutAToken_WithGarbage_OrWithAPlatformSession_BothRoutesAre401_AndNothingIsClosed()
    {
        var account = await fixture.RegisterOwnerAsync();
        var platformToken = await fixture.PlatformTokenAsync(await fixture.CreateOperatorAsync());
        var someId = SidOf(account.AccessToken).ToString();

        foreach (var token in new string?[] { null, "esto-no-es-un-token", platformToken })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await ListAsync(token)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await CloseAsync(token, someId)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await ListAsync(account.AccessToken)).StatusCode);                    // control: la sesión sigue viva
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_GetsTheSuspensionError_OnBothRoutes()
    {
        var owner = await fixture.RegisterOwnerAsync($"Empresa {Guid.NewGuid():N}");
        Guid tenantId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            tenantId = (await db.TenantUsers.IgnoreQueryFilters().SingleAsync(tu => tu.UserId == Guid.Parse(UserIdOf(owner.AccessToken)))).TenantId;
        }

        var platformToken = await fixture.PlatformTokenAsync(await fixture.CreateOperatorAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenantId}/suspend", bearer: platformToken)).StatusCode);

        var list = await ListAsync(owner.AccessToken);
        var close = await CloseAsync(owner.AccessToken, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
        Assert.Equal("tenant.suspended", (await MfaFlowFixture.JsonOf(list)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task EveryRole_CanListItsSessions_AndCloseAnotherOfItsOwn(TenantRole role)
    {
        var owner = await fixture.RegisterOwnerAsync();
        var person = role == TenantRole.Owner ? owner : await fixture.InviteAndJoinAsync(owner, role);
        var other = await fixture.OpenExtraSessionTokenAsync(person);

        Assert.Equal(HttpStatusCode.OK, (await ListAsync(person.AccessToken)).StatusCode);
        var close = await CloseAsync(person.AccessToken, SidOf(other).ToString());

        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListAsync(other)).StatusCode); // la cerrada ya no vale
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(person.AccessToken)).StatusCode); // la propia sigue
    }

    // ===== Cerrar una =====

    [Fact]
    public async Task ClosingAnotherDevice_CutsItsTokens_AndLeavesThisOneWorking()
    {
        var account = await fixture.RegisterOwnerAsync();
        var chrome = await LoginAsync(account, ChromeWindows);
        var firefox = await LoginAsync(account, FirefoxLinux);
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(firefox.Access)).StatusCode); // control: el otro dispositivo funcionaba

        var response = await CloseAsync(chrome.Access, firefox.Sid.ToString());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListAsync(firefox.Access)).StatusCode);                                     // su access token, al instante
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(chrome.Access)).StatusCode);                                                 // este sigue
        Assert.DoesNotContain(await SessionsOfAsync(chrome.Access), s => s.GetProperty("id").GetString() == firefox.Sid.ToString());
        // Y su refresh token tampoco renueva. (Va el último a propósito: usar un refresh token cerrado es una REUTILIZACIÓN y revoca el resto de la cuenta; ver el test de abajo.)
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = firefox.Refresh })).StatusCode);
    }

    [Fact]
    public async Task UsingTheClosedSessionsRefreshTokenAfterwards_IsReuse_AndRevokesTheRestOfTheAccount_WhichIsTheKnownCost()
    {
        // R13 y la spec 014 D3: se REVOCA, no se borra, para que quien use después una copia del token cerrado dispare la detección de reutilización (1003).
        // El coste, conocido y documentado: esa reutilización revoca también las demás sesiones activas de la cuenta (falla hacia cerrar).
        var account = await fixture.RegisterOwnerAsync();
        var chrome = await LoginAsync(account, ChromeWindows);
        var firefox = await LoginAsync(account, FirefoxLinux);
        await CloseAsync(chrome.Access, firefox.Sid.ToString());
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(chrome.Access)).StatusCode); // control: antes de la reutilización, esta sigue

        var reuse = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = firefox.Refresh });

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListAsync(chrome.Access)).StatusCode);
        Assert.Contains(fixture.Logs.Entries, e => e.EventId == SecurityEventIds.RefreshTokenReuseDetected && e.Message.Contains(UserIdOf(account.AccessToken)));
    }

    [Fact]
    public async Task ClosingTheCurrentSession_IsA204_AndThenTheTokenIsA401()
    {
        var account = await fixture.RegisterOwnerAsync();
        var login = await LoginAsync(account, ChromeWindows);

        var response = await CloseAsync(login.Access, login.Sid.ToString());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ListAsync(login.Access)).StatusCode);
    }

    [Fact]
    public async Task ThreeDifferentFailures_AreTheSameAnswer_SoTheRouteIsNoOracleOfOthersSessions()
    {
        var attacker = await fixture.RegisterOwnerAsync($"Empresa A {Guid.NewGuid():N}");
        var victim = await fixture.RegisterOwnerAsync($"Empresa B {Guid.NewGuid():N}");
        var closed = await LoginAsync(attacker, FirefoxLinux);
        await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", new { refreshToken = closed.Refresh }); // una sesión suya que ya está cerrada

        var ofAnotherAccount = await CloseAsync(attacker.AccessToken, SidOf(victim.AccessToken).ToString()); // existe y está activa, pero es de otra cuenta
        var unknown = await CloseAsync(attacker.AccessToken, Guid.NewGuid().ToString());                   // no existe
        var alreadyClosed = await CloseAsync(attacker.AccessToken, closed.Sid.ToString());                 // existió, ya cerrada

        foreach (var response in new[] { ofAnotherAccount, unknown, alreadyClosed })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Lo observable es idéntico: mismo tipo de contenido, mismo código de error, mismo texto, mismo estado. (El traceId de cada respuesta es lo único que cambia.)
        static async Task<string> Shape(HttpResponseMessage r)
        {
            var body = (await MfaFlowFixture.JsonOf(r)).EnumerateObject().Where(p => p.Name != "traceId").Select(p => $"{p.Name}={p.Value}");
            return $"{(int)r.StatusCode}|{r.Content.Headers.ContentType}|{string.Join(";", body)}|{string.Join(";", r.Headers.Where(h => h.Key is "Cache-Control").Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}";
        }

        var shapes = new[] { await Shape(ofAnotherAccount), await Shape(unknown), await Shape(alreadyClosed) };
        Assert.Single(shapes.Distinct());
        Assert.Contains("session.not_found", shapes[0]);

        // Y la víctima no perdió nada.
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(victim.AccessToken)).StatusCode);
    }

    [Theory]
    [InlineData("no-es-un-guid")]
    [InlineData("12345")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task AnIdThatIsNotARealSession_IsA404_AndNothingIsClosed(string id)
    {
        var account = await fixture.RegisterOwnerAsync();

        var response = await CloseAsync(account.AccessToken, id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(account.AccessToken)).StatusCode); // la propia sesión sigue viva
    }

    [Fact]
    public async Task AUserIdInTheQueryOrTheBody_IsIgnored_ThereIsNoOtherUserIdThatCanComeFromOutside()
    {
        var attacker = await fixture.RegisterOwnerAsync($"Empresa A {Guid.NewGuid():N}");
        var victim = await fixture.RegisterOwnerAsync($"Empresa B {Guid.NewGuid():N}");
        var attackerOther = await LoginAsync(attacker, FirefoxLinux);
        var victimUserId = UserIdOf(victim.AccessToken);

        // El atacante cierra una sesión SUYA pero adjunta el userId de la víctima en la consulta y en el cuerpo.
        var response = await CloseAsync(attacker.AccessToken, attackerOther.Sid.ToString(), query: $"?userId={victimUserId}", body: new { userId = victimUserId });
        // Y pide cerrar la sesión de la víctima con su Id real y el userId de la víctima.
        var onTheVictim = await CloseAsync(attacker.AccessToken, SidOf(victim.AccessToken).ToString(), query: $"?userId={victimUserId}", body: new { userId = victimUserId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);                  // cerró la suya
        Assert.Equal(HttpStatusCode.NotFound, onTheVictim.StatusCode);                // con la de la víctima no pudo, aunque nombrara a la víctima
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(victim.AccessToken)).StatusCode);
    }

    // ===== El evento =====

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingASession_LeavesOneEvent_WithTheUserAndWhetherItWasTheCurrent_AndNoSessionIdNorToken(bool current)
    {
        var account = await fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account.AccessToken);
        var other = await LoginAsync(account, FirefoxLinux);
        var mine = await LoginAsync(account, ChromeWindows);
        var target = current ? mine : other;
        Assert.Empty(ClosedByOwnerEventsOf(userId));

        await CloseAsync(mine.Access, target.Sid.ToString());

        var entry = Assert.Single(ClosedByOwnerEventsOf(userId));
        Assert.Contains($"era la sesión actual: {current}", entry.Message);
        var wholeLog = string.Join("\n", fixture.Logs.Entries.Select(e => e.Message));
        foreach (var secret in new[] { target.Sid.ToString(), target.Refresh, target.Access, mine.Refresh, mine.Access })
        {
            Assert.DoesNotContain(secret, wholeLog);
        }
    }

    [Fact]
    public async Task ACloseThatFinds_NoSession_LeavesNoEvent()
    {
        var account = await fixture.RegisterOwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(account.AccessToken, Guid.NewGuid().ToString())).StatusCode);

        Assert.Empty(ClosedByOwnerEventsOf(UserIdOf(account.AccessToken)));
    }

    // ===== El límite de peticiones, compartido con «cerrar todas» =====

    [Fact]
    public async Task TheBucket_IsSharedWithCloseAll_AndTheEleventhCallFromTheSameIpIs429()
    {
        var account = await fixture.RegisterOwnerAsync();
        var ip = TestClientIpStartupFilter.NewIp();

        // Nueve «cerrar una» (404: no importa que no exista, cuenta igual) y una «cerrar todas» (204) gastan las diez del cubo `account-sessions`.
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(account.AccessToken, Guid.NewGuid().ToString(), ip)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Http.SendAsync(Request(HttpMethod.Delete, SessionsPath, account.AccessToken, ip: ip))).StatusCode);

        var rejected = await CloseAsync(account.AccessToken, Guid.NewGuid().ToString(), ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        // La ventana es de UNA HORA, no de un minuto: Retry-After es lo que queda de ella (hasta 3600 s).
        Assert.InRange(int.Parse(rejected.Headers.GetValues("Retry-After").Single()), 61, 3600);
    }

    [Fact]
    public async Task ListingHasNoLimitOfItsOwn_AndDoesNotSpendTheBucketOfClosing()
    {
        var account = await fixture.RegisterOwnerAsync();
        var ip = TestClientIpStartupFilter.NewIp();

        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await ListAsync(account.AccessToken, ip)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(account.AccessToken, Guid.NewGuid().ToString(), ip)).StatusCode); // el cubo de cerrar sigue entero
    }
}

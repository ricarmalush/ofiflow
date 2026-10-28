using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// «Cerrar todas mis sesiones» (spec 014) de punta a punta, con la API entera y SQL Server real: los tres dispositivos, el
/// aislamiento entre cuentas, que nada de la petición pueda apuntar a otra persona, quién puede llamar y quién no, el evento y el
/// límite de peticiones. Cada denegación va con su control positivo. Todas las peticiones llevan una IP distinta salvo las del límite.
/// </summary>
public sealed class CloseAllSessionsEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string SessionsPath = "/api/v1/account/sessions";
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string LogoutPath = "/api/v1/auth/logout";

    private readonly PlatformFlowFixture _fixture = fixture;

    private Task<HttpResponseMessage> CloseAllAsync(string? token, string query = "", object? body = null) =>
        _fixture.SendAsync(HttpMethod.Delete, SessionsPath + query, body, token);

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _fixture.SendAsync(HttpMethod.Post, RefreshPath, new { refreshToken });

    private static string UserIdOf(MfaFlowFixture.Account account) =>
        new JwtSecurityTokenHandler().ReadJwtToken(account.AccessToken).Subject;

    private List<CapturingLoggerProvider.LogEntry> EventsOf(int eventId, string userId) =>
        _fixture.Logs.Entries.Where(e => e.EventId == eventId && e.Message.Contains(userId)).ToList();

    /// <summary>Abre un dispositivo más de la cuenta y devuelve su refresh token (el login lo deja también en la cuenta).</summary>
    private async Task<string> NewDeviceAsync(MfaFlowFixture.Account account)
    {
        await _fixture.LoginPlainAsync(account);
        return account.RefreshToken;
    }

    // ===== Cierra todas =====

    [Fact]
    public async Task ThreeDevices_AreAllClosedByOneCall_AndNoneRenewsAnymore()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var laptop = account.RefreshToken;
        var phone = await NewDeviceAsync(account);
        var tablet = await NewDeviceAsync(account);
        Assert.Equal(3, new[] { laptop, phone, tablet }.Distinct().Count());

        var response = await CloseAllAsync(account.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        foreach (var token in new[] { laptop, phone, tablet })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(token)).StatusCode);
        }
    }

    [Fact]
    public async Task TheDeviceThatAsks_IsClosedToo_AndALaterLoginStartsAFreshSession()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var thisDevice = account.RefreshToken;
        Assert.Equal(HttpStatusCode.NoContent, (await CloseAllAsync(account.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(thisDevice)).StatusCode);

        // Control: cerrar las sesiones no cierra la cuenta; con su contraseña vuelve a entrar y esa sesión sí renueva.
        var fresh = await NewDeviceAsync(account);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(fresh)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheAccountThatAsks_IsClosed_AnotherAccountKeepsRenewing()
    {
        var a = await _fixture.RegisterOwnerAsync($"Empresa A {Guid.NewGuid():N}");
        var b = await _fixture.RegisterOwnerAsync($"Empresa B {Guid.NewGuid():N}");
        var bPhone = await NewDeviceAsync(b);

        await CloseAllAsync(a.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(a.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(bPhone)).StatusCode);
    }

    [Fact]
    public async Task ACompanysOtherMember_IsNotTouched_ByTheOwnerClosingTheirOwnSessions()
    {
        // Misma empresa, otra persona: cerrar «mis» sesiones no es cerrar las de mi equipo.
        var owner = await _fixture.RegisterOwnerAsync();
        var member = await _fixture.InviteAndJoinAsync(owner, TenantRole.Employee);

        await CloseAllAsync(owner.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(owner.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(member.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task AnotherAccountsId_InTheQueryOrTheBody_IsIgnored_TheCallerIsTheOneClosed()
    {
        // R1: la ruta no tiene Id. Aunque la petición traiga el de otra cuenta por todas partes, solo se cierra la de quien llama.
        var attacker = await _fixture.RegisterOwnerAsync();
        var victim = await _fixture.RegisterOwnerAsync();
        var victimId = UserIdOf(victim);

        var response = await CloseAllAsync(
            attacker.AccessToken,
            $"?userId={victimId}&UserId={victimId}&email={victim.Email}",
            new { userId = victimId, sub = victimId, email = victim.Email });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(attacker.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(victim.RefreshToken)).StatusCode);
        Assert.Empty(EventsOf(SecurityEventIds.AllSessionsClosed, victimId));
    }

    [Fact]
    public async Task APathWithAnId_DoesNotExist_ThereIsNoRouteToCloseSomeoneElsesSessions()
    {
        var attacker = await _fixture.RegisterOwnerAsync();
        var victim = await _fixture.RegisterOwnerAsync();

        var response = await CloseAllAsync(attacker.AccessToken, query: string.Empty);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); // control: la ruta sin Id existe

        var withAnId = await _fixture.SendAsync(HttpMethod.Delete, $"{SessionsPath}/{UserIdOf(victim)}", bearer: attacker.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, withAnId.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(victim.RefreshToken)).StatusCode);
    }

    // ===== Idempotente, sin sesiones =====

    [Fact]
    public async Task WithNoActiveSessions_ItIsStill204_AndTheEventCarriesAZero()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        Assert.Equal(HttpStatusCode.NoContent, (await CloseAllAsync(account.AccessToken)).StatusCode); // cierra la única

        // El access token sigue valiendo (límite conocido), así que se puede pedir otra vez: ya no hay ninguna activa.
        var again = await CloseAllAsync(account.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var events = EventsOf(SecurityEventIds.AllSessionsClosed, userId);
        Assert.Equal(2, events.Count); // uno por petición (R6)
        Assert.Contains("sesiones cerradas 1", events[0].Message);
        Assert.Contains("sesiones cerradas 0", events[1].Message);
    }

    [Fact]
    public async Task AnAlreadyClosedDevice_IsNotCounted_AndItsRowIsNotTouched()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        var closedBefore = account.RefreshToken;
        var phone = await NewDeviceAsync(account);
        await NewDeviceAsync(account);
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken = closedBefore })).StatusCode);
        Assert.NotEqual(closedBefore, phone);

        await CloseAllAsync(account.AccessToken);

        // Quedaban 2 activas de 3: el recuento no incluye la que ya estaba cerrada.
        Assert.Contains("sesiones cerradas 2", Assert.Single(EventsOf(SecurityEventIds.AllSessionsClosed, userId)).Message);
    }

    // ===== Un token cerrado que se usa después =====

    [Fact]
    public async Task UsingATokenClosedByThisOperation_IsReuseOfARevokedToken_401AndEvent1003()
    {
        // R3: se revoca, no se borra. Quien use después una copia de uno de esos tokens dispara la detección de reutilización.
        var account = await _fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        var stolenCopy = account.RefreshToken;
        await CloseAllAsync(account.AccessToken);
        Assert.Empty(EventsOf(SecurityEventIds.RefreshTokenReuseDetected, userId));

        var attempt = await RefreshAsync(stolenCopy);

        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        Assert.Single(EventsOf(SecurityEventIds.RefreshTokenReuseDetected, userId));
    }

    [Fact]
    public async Task TheAccessTokenAlreadyIssued_KeepsWorkingUntilItExpires_ThisIsTheKnownLimit()
    {
        // Lo que esta spec NO hace (ver «Qué NO incluye»): documentarlo en un test evita que alguien crea que sí.
        var account = await _fixture.RegisterOwnerAsync();
        await CloseAllAsync(account.AccessToken);

        var withOldAccessToken = await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: account.AccessToken);

        Assert.Equal(HttpStatusCode.OK, withOldAccessToken.StatusCode);
    }

    // ===== Quién puede llamar =====

    [Fact]
    public async Task WithoutAToken_WithGarbage_OrWithAPlatformSession_ItIsA401_AndNothingIsClosed()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var platformToken = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await CloseAllAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CloseAllAsync("esto-no-es-un-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CloseAllAsync(platformToken)).StatusCode); // el operador no es una persona de empresa

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(account.RefreshToken)).StatusCode); // control: la sesión sigue viva
        Assert.Equal(HttpStatusCode.NoContent, (await CloseAllAsync(account.AccessToken)).StatusCode); // control: con token de empresa, sí
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_GetsTheSuspensionError_AndNothingIsClosedNorLogged()
    {
        var owner = await _fixture.RegisterOwnerAsync($"Empresa {Guid.NewGuid():N}");
        var ownerId = UserIdOf(owner);
        Guid tenantId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            tenantId = (await db.TenantUsers.IgnoreQueryFilters().SingleAsync(tu => tu.UserId == Guid.Parse(ownerId))).TenantId;
        }

        var platformToken = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenantId}/suspend", bearer: platformToken)).StatusCode);

        var response = await CloseAllAsync(owner.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant.suspended", (await MfaFlowFixture.JsonOf(response)).GetProperty("code").GetString());
        Assert.Empty(EventsOf(SecurityEventIds.AllSessionsClosed, ownerId));
    }

    [Fact]
    public async Task AMemberWhoWasRemoved_WithAStillValidToken_GetsA403_AndClosesNothing()
    {
        var owner = await _fixture.RegisterOwnerAsync();
        var member = await _fixture.InviteAndJoinAsync(owner, TenantRole.Employee);
        var memberId = UserIdOf(member);
        var membershipId = await _fixture.TenantUserIdOfAsync(owner, member);
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{membershipId}", bearer: owner.AccessToken)).StatusCode);

        var response = await CloseAllAsync(member.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(EventsOf(SecurityEventIds.AllSessionsClosed, memberId));
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task EveryRole_CanCloseItsOwnSessions(TenantRole role)
    {
        var owner = await _fixture.RegisterOwnerAsync();
        var person = role == TenantRole.Owner ? owner : await _fixture.InviteAndJoinAsync(owner, role);
        var otherDevice = await NewDeviceAsync(person);

        var response = await CloseAllAsync(person.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(otherDevice)).StatusCode);
    }

    // ===== Qué deja =====

    [Fact]
    public async Task TheResponse_IsNeverCached_AndHasNoBody()
    {
        var account = await _fixture.RegisterOwnerAsync();

        var response = await CloseAllAsync(account.AccessToken);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheEvent_CarriesTheUserAndTheCount_NeverTokensHashesNorEmails()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        var first = account.RefreshToken;
        var second = await NewDeviceAsync(account);
        var hashes = new[] { first, second }
            .Select(t => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t))))
            .ToList();

        await CloseAllAsync(account.AccessToken);

        var entry = Assert.Single(EventsOf(SecurityEventIds.AllSessionsClosed, userId));
        Assert.Contains("sesiones cerradas 2", entry.Message);
        Assert.DoesNotContain(account.Email, entry.Message);

        // Ni en ese evento ni en ninguna otra línea del registro.
        var wholeLog = string.Join("\n", _fixture.Logs.Entries.Select(e => e.Message));
        foreach (var secret in new[] { first, second, account.AccessToken }.Concat(hashes))
        {
            Assert.DoesNotContain(secret, wholeLog);
        }
    }

    // ===== El límite de peticiones (R7) =====

    [Fact]
    public async Task ElevenCalls_FromOneIp_AreRejectedWith429_AndTheLogoutBucketIsNotSpent()
    {
        var account = await _fixture.RegisterOwnerAsync();
        var ip = TestClientIpStartupFilter.NewIp();

        HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            return request;
        }

        // El access token sigue valiendo tras cerrar (límite conocido), así que se pueden repetir las diez permitidas.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await _fixture.Http.SendAsync(NewRequest(HttpMethod.Delete, SessionsPath))).StatusCode);
        }

        var rejected = await _fixture.Http.SendAsync(NewRequest(HttpMethod.Delete, SessionsPath));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));

        // La ventana es de UNA HORA, no de un minuto: con una ventana fija, Retry-After es lo que queda de ella (hasta 3600 s). Si fuera
        // de un minuto no pasaría de 60. Es la forma de comprobar la ventana sin esperarla.
        Assert.InRange(int.Parse(rejected.Headers.GetValues("Retry-After").Single()), 61, 3600);

        // Cubo propio: desde la misma IP, la lectura de «quién soy» (sin límite) y cerrar sesión (otro cubo) siguen respondiendo lo suyo.
        Assert.Equal(HttpStatusCode.OK, (await _fixture.Http.SendAsync(NewRequest(HttpMethod.Get, "/api/v1/account/me"))).StatusCode);
        var logout = NewRequest(HttpMethod.Post, LogoutPath);
        logout.Content = System.Net.Http.Json.JsonContent.Create(new { refreshToken = "no-existe" });
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.Http.SendAsync(logout)).StatusCode);
    }
}

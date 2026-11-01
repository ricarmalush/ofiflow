using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Cerrar sesión de principio a fin por HTTP (spec 013, bloque 3), con la API entera y SQL Server real. Cada denegación
/// va con su control positivo. Todas las peticiones llevan una IP distinta salvo las del límite de peticiones.
/// </summary>
public class LogoutEndToEndTests(MfaFlowFixture fixture) : IClassFixture<MfaFlowFixture>
{
    private const string LogoutPath = "/api/v1/auth/logout";
    private const string RefreshPath = "/api/v1/auth/refresh";

    private Task<HttpResponseMessage> LogoutAsync(string refreshToken) =>
        fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken });

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        fixture.SendAsync(HttpMethod.Post, RefreshPath, new { refreshToken });

    private static string UserIdOf(MfaFlowFixture.Account account) =>
        new JwtSecurityTokenHandler().ReadJwtToken(account.AccessToken).Subject;

    private IEnumerable<CapturingLoggerProvider.LogEntry> EventsOf(int eventId, string userId) =>
        fixture.Logs.Entries.Where(e => e.EventId == eventId && e.Message.Contains(userId));

    // ===== Cierra de verdad =====

    [Fact]
    public async Task ClosingASession_Gives204_AndTheTokenNoLongerRenews()
    {
        var account = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(account.RefreshToken)).StatusCode); // control: antes de cerrar, otro token sirve
        await fixture.LoginPlainAsync(account);

        var logout = await LogoutAsync(account.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(0, logout.Content.Headers.ContentLength ?? 0);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(account.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task UsingAClosedToken_IsReuseOfARevokedToken_EventAndChainRevoked()
    {
        // R6: un token que el cliente legítimo cerró y alguien usa después es una copia.
        var account = await fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        var closedToken = account.RefreshToken;
        await fixture.LoginPlainAsync(account); // segundo dispositivo
        var otherDevice = account.RefreshToken;
        await LogoutAsync(closedToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(closedToken)).StatusCode);

        Assert.Single(EventsOf(SecurityEventIds.RefreshTokenReuseDetected, userId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(otherDevice)).StatusCode); // la cadena cayó
    }

    [Fact]
    public async Task TheAccessTokenOfTheClosedSession_IsRejectedAtOnce_NotAfter30Minutes()
    {
        // Hasta la spec 015 este test se llamaba «...KeepsWorkingUntilItExpires_ThisIsTheKnownLimit» y esperaba un 200: el access token
        // ya emitido valía hasta 30 minutos tras cerrar la sesión. Ahora la sesión cerrada deja sin efecto su token al instante.
        var account = await fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: account.AccessToken)).StatusCode); // control
        await LogoutAsync(account.RefreshToken);

        var withOldAccessToken = await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: account.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, withOldAccessToken.StatusCode);
        Assert.Single(EventsOf(SecurityEventIds.SessionTokenRejected, userId));
    }

    [Fact]
    public async Task ClosingOneDevice_LeavesTheAccessTokenOfTheOtherDeviceWorking()
    {
        // El «sid» es por sesión, no por cuenta (spec 015 R7): cerrar el portátil no echa al móvil.
        var account = await fixture.RegisterOwnerAsync();
        var laptopAccess = account.AccessToken;
        var laptopRefresh = account.RefreshToken;
        await fixture.LoginPlainAsync(account);
        var phoneAccess = account.AccessToken;
        Assert.NotEqual(laptopAccess, phoneAccess);

        await LogoutAsync(laptopRefresh);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: laptopAccess)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: phoneAccess)).StatusCode);
    }

    [Fact]
    public async Task ClosingNeedsNoAccessToken_AndWorksWithAGarbageAuthorizationHeader()
    {
        var account = await fixture.RegisterOwnerAsync();

        var logout = await fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken = account.RefreshToken }, bearer: "caducado-o-basura");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(account.RefreshToken)).StatusCode);
    }

    // ===== Solo esa sesión =====

    [Fact]
    public async Task ClosingOneDevice_LeavesTheOtherDeviceOfTheSameAccountWorking()
    {
        var account = await fixture.RegisterOwnerAsync();
        var laptop = account.RefreshToken;
        await fixture.LoginPlainAsync(account);
        var phone = account.RefreshToken;
        Assert.NotEqual(laptop, phone);

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(laptop)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(phone)).StatusCode);
    }

    [Fact]
    public async Task ClosingWithATokenOfOneCompany_DoesNotTouchAnotherCompany()
    {
        var a = await fixture.RegisterOwnerAsync("Empresa A del cierre");
        var b = await fixture.RegisterOwnerAsync("Empresa B del cierre");

        await LogoutAsync(a.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(a.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(b.RefreshToken)).StatusCode);
    }

    // ===== Sin oráculo, idempotente =====

    [Fact]
    public async Task ClosingTwice_AnInventedToken_AndARotatedToken_AllGive204_AndLeaveEverythingElseAlone()
    {
        var account = await fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        var rotated = account.RefreshToken;
        var renewed = await MfaFlowFixture.JsonOf(await RefreshAsync(rotated));
        var current = renewed.GetProperty("refreshToken").GetString()!;

        // Un token ya rotado: cerrar con él NO es una reutilización (R3), el token actual sigue vivo.
        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(rotated)).StatusCode);
        Assert.Empty(EventsOf(SecurityEventIds.RefreshTokenReuseDetected, userId));
        Assert.Empty(EventsOf(SecurityEventIds.SessionClosed, userId));

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(Convert.ToBase64String(new byte[64]))).StatusCode); // inventado

        // El cierre real, y repetirlo.
        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(current)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(current)).StatusCode);
        Assert.Single(EventsOf(SecurityEventIds.SessionClosed, userId));
        Assert.Empty(EventsOf(SecurityEventIds.RefreshTokenReuseDetected, userId));
    }

    [Fact]
    public async Task TheResponse_IsIdentical_ForARealTokenAndAnInventedOne()
    {
        var account = await fixture.RegisterOwnerAsync();

        var real = await MfaFlowFixture.FingerprintAsync(await LogoutAsync(account.RefreshToken));
        var invented = await MfaFlowFixture.FingerprintAsync(await LogoutAsync(Convert.ToBase64String(new byte[64])));

        Assert.Equal(real, invented);
    }

    // ===== La empresa no bloquea el cierre (R5) =====

    [Fact]
    public async Task ASuspendedCompany_CanStillCloseItsSession()
    {
        var account = await fixture.RegisterOwnerAsync("Empresa suspendida del cierre");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Name == "Empresa suspendida del cierre");
            tenant.Suspend(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(account.RefreshToken)).StatusCode);
        Assert.Single(EventsOf(SecurityEventIds.SessionClosed, UserIdOf(account)));
    }

    [Fact]
    public async Task AMemberWhoWasRemoved_CanStillCallLogout_Gets204()
    {
        var owner = await fixture.RegisterOwnerAsync();
        var member = await fixture.InviteAndJoinAsync(owner, TenantRole.Employee);
        var membershipId = await fixture.TenantUserIdOfAsync(owner, member);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{membershipId}", bearer: owner.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(member.RefreshToken)).StatusCode);
    }

    // ===== La forma de la entrada (R4) =====

    [Fact]
    public async Task ABodyWithoutAUsableToken_Gives400_NotAnything204()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await LogoutAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LogoutAsync(new string('a', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.SendAsync(HttpMethod.Post, LogoutPath, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.SendAsync(HttpMethod.Post, LogoutPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(new string('a', 200))).StatusCode); // control: 200 caracteres es forma válida
    }

    // ===== El evento (R7) =====

    [Fact]
    public async Task TheEvent_CarriesOnlyIds_NeverTheTokenNorItsHash()
    {
        var account = await fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account);
        var token = account.RefreshToken;
        var hash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        await LogoutAsync(token);

        var entry = Assert.Single(EventsOf(SecurityEventIds.SessionClosed, userId));
        Assert.DoesNotContain(token, entry.Message);
        Assert.DoesNotContain(hash, entry.Message);
        Assert.DoesNotContain(account.Email, entry.Message);

        // Ni en ese evento ni en ninguna otra línea del registro.
        var wholeLog = string.Join("\n", fixture.Logs.Entries.Select(e => e.Message));
        Assert.DoesNotContain(token, wholeLog);
        Assert.DoesNotContain(hash, wholeLog);
    }

    // ===== El límite de peticiones (R8) =====

    [Fact]
    public async Task TwentyOneCloses_FromOneIp_AreRejectedWith429_AndRefreshKeepsItsOwnBucket()
    {
        var ip = TestClientIpStartupFilter.NewIp();

        HttpRequestMessage NewRequest(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { refreshToken = "" }) };
            request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
            return request;
        }

        // Cuerpo vacío: 400 sin tocar la base de datos, pero cada intento cuenta.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Http.SendAsync(NewRequest(LogoutPath))).StatusCode);
        }

        var rejected = await fixture.Http.SendAsync(NewRequest(LogoutPath));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));

        // Cerrar sesión no gasta el presupuesto de renovar: desde la misma IP, /auth/refresh responde lo suyo (400), no 429.
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Http.SendAsync(NewRequest(RefreshPath))).StatusCode);
    }
}

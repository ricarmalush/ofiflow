using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Los datos de la sesión (spec 016, bloque 2) de punta a punta, con la API entera y SQL Server real: lo que dice el cliente en su petición (la IP y el
/// <c>User-Agent</c>) llega, <b>truncado y saneado</b>, a la fila del refresh token; y una renovación desde otro cliente NO cambia el dispositivo de la
/// sesión. Prueba el cableado que los tests de Infrastructure no ven (el servidor de verdad, HttpContext, el registro de IClientInfo).
/// </summary>
public class SessionDataEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private HttpRequestMessage Request(string path, object body, string? ip, string? userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (ip is not null)
        {
            request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
        }

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return request;
    }

    private static string UserIdOf(string accessToken) => new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;

    private async Task<(string Access, string Refresh)> LoginAsync(MfaFlowFixture.Account account, string? ip, string? userAgent)
    {
        var response = await fixture.Http.SendAsync(Request("/api/v1/auth/login", new { email = account.Email, password = account.Password }, ip ?? TestClientIpStartupFilter.NewIp(), userAgent));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await MfaFlowFixture.JsonOf(response);

        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    private async Task<List<RefreshToken>> RowsOfAsync(string userId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Set<RefreshToken>().AsNoTracking().Where(rt => rt.UserId == Guid.Parse(userId)).OrderBy(rt => rt.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task ALogin_StoresTheTruncatedNetworkAndTheBrowserOfTheRequest()
    {
        var account = await fixture.RegisterOwnerAsync();
        var (access, _) = await LoginAsync(account, ip: "203.0.113.77", userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0");

        var row = (await RowsOfAsync(UserIdOf(access))).Single(rt => rt.UserAgent is not null);

        Assert.Equal("203.0.113.0", row.IpAddress);                                  // el último octeto no se guarda
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0", row.UserAgent);
        Assert.InRange(row.SessionStartedAt, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow);
        Assert.Equal(row.CreatedAt, row.SessionStartedAt);
    }

    [Fact]
    public async Task TheFullAddress_IsNeverStoredAnywhere_NotEvenInTheOtherRowsOfTheAccount()
    {
        const string fullIp = "198.51.100.231";
        var account = await fixture.RegisterOwnerAsync();
        var (access, refresh) = await LoginAsync(account, ip: fullIp, userAgent: "Firefox/121.0");
        await fixture.Http.SendAsync(Request("/api/v1/auth/refresh", new { refreshToken = refresh }, fullIp, "Firefox/121.0")); // y una renovación desde la misma IP

        var rows = await RowsOfAsync(UserIdOf(access));

        Assert.True(rows.Count >= 3); // el registro, el login y la renovación
        Assert.All(rows, rt => Assert.DoesNotContain(fullIp, rt.IpAddress ?? string.Empty));
        Assert.All(rows, rt => Assert.NotEqual(fullIp, rt.IpAddress));
    }

    [Fact]
    public async Task ARenewalFromAnotherClient_DoesNotChangeTheDeviceOfTheSession()
    {
        var account = await fixture.RegisterOwnerAsync();
        var (access, refresh) = await LoginAsync(account, ip: "203.0.113.50", userAgent: "Chrome/120.0 (Windows)");
        await Task.Delay(30);

        // La misma sesión se renueva desde otro navegador y otra red (un token copiado, o simplemente un cambio de red del propio usuario).
        var renewal = await fixture.Http.SendAsync(Request("/api/v1/auth/refresh", new { refreshToken = refresh }, "198.51.100.9", "Safari/17.0 (iPhone)"));
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);

        var rows = (await RowsOfAsync(UserIdOf(access))).Where(rt => rt.UserAgent is not null).OrderBy(rt => rt.CreatedAt).ToList();
        Assert.Equal(2, rows.Count);                                    // la del login y la de la renovación
        Assert.All(rows, rt => Assert.Equal("Chrome/120.0 (Windows)", rt.UserAgent)); // el navegador de siempre
        Assert.All(rows, rt => Assert.Equal("203.0.113.0", rt.IpAddress));            // la red de siempre
        Assert.Equal(rows[0].SessionStartedAt, rows[1].SessionStartedAt);              // cuándo empezó: heredado
        Assert.True(rows[1].CreatedAt > rows[0].CreatedAt);                              // la última actividad: la de la renovación
        Assert.Equal(rows[0].SessionId, rows[1].SessionId);
    }


    [Fact]
    public async Task AnEnormousBrowser_IsStoredCutToTheLimit_AndTheLoginStillWorks()
    {
        var account = await fixture.RegisterOwnerAsync();

        var (access, _) = await LoginAsync(account, ip: null, userAgent: new string('x', 5000));

        var row = (await RowsOfAsync(UserIdOf(access))).Single(rt => rt.UserAgent is not null);
        Assert.Equal(256, row.UserAgent!.Length);
    }

    [Fact]
    public async Task ALoginWithoutABrowserHeader_StillWorks_AndStoresNoBrowser()
    {
        var account = await fixture.RegisterOwnerAsync();

        var (access, _) = await LoginAsync(account, ip: "203.0.113.5", userAgent: null);

        var rows = await RowsOfAsync(UserIdOf(access));
        Assert.Contains(rows, rt => rt.UserAgent is null && rt.IpAddress == "203.0.113.0");
    }

    [Fact]
    public async Task EveryLogin_IsItsOwnSession_WithItsOwnDevice()
    {
        var account = await fixture.RegisterOwnerAsync();
        var (access, _) = await LoginAsync(account, ip: "203.0.113.1", userAgent: "Chrome/120 (Windows)");
        await Task.Delay(30);
        await LoginAsync(account, ip: "198.51.100.2", userAgent: "Safari/17 (iPhone)");

        var rows = (await RowsOfAsync(UserIdOf(access))).Where(rt => rt.UserAgent is not null).OrderBy(rt => rt.SessionStartedAt).ToList();

        Assert.Equal(["Chrome/120 (Windows)", "Safari/17 (iPhone)"], rows.Select(rt => rt.UserAgent!));
        Assert.Equal(["203.0.113.0", "198.51.100.0"], rows.Select(rt => rt.IpAddress!));
        Assert.NotEqual(rows[0].SessionId, rows[1].SessionId);
    }
}

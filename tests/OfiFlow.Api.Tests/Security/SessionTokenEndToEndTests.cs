using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El access token solo vale mientras su sesión siga activa (spec 015), de punta a punta, con la API entera y SQL Server real: cada
/// inicio de sesión tiene su Id, renovar lo conserva, cerrar la sesión (por cualquier vía) deja su token sin efecto al instante, y solo
/// el de esa sesión. Cada rechazo va con su control positivo (el mismo token valía antes de cerrar).
/// </summary>
public sealed class SessionTokenEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string CustomersPath = "/api/v1/customers";
    private const string LogoutPath = "/api/v1/auth/logout";
    private const string RefreshPath = "/api/v1/auth/refresh";

    private Task<HttpResponseMessage> GetAsync(string path, string? token) => fixture.SendAsync(HttpMethod.Get, path, bearer: token);

    private static JwtSecurityToken Read(string accessToken) => new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

    private static string SidOf(string accessToken) => Read(accessToken).Claims.Single(c => c.Type == "sid").Value;

    private static string UserIdOf(string accessToken) => Read(accessToken).Subject;

    private async Task<(string Access, string Refresh)> RefreshAsync(string refreshToken)
    {
        var response = await fixture.SendAsync(HttpMethod.Post, RefreshPath, new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await MfaFlowFixture.JsonOf(response);
        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>Un token firmado con el secreto de la API, con los claims que se le den: para formas que el login nunca emite.</summary>
    private string Mint(params Claim[] claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = fixture.Database.JwtIssuer,
            Audience = fixture.Database.JwtAudience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(fixture.Database.JwtSecret)), SecurityAlgorithms.HmacSha256)
        });

    private List<CapturingLoggerProvider.LogEntry> RejectionsOf(string userId) =>
        fixture.Logs.Entries.Where(e => e.EventId == SecurityEventIds.SessionTokenRejected && e.Message.Contains(userId)).ToList();

    // ===== Cada sesión, su Id =====

    [Fact]
    public async Task EveryLoginHasItsOwnSid_AndARenewalKeepsIt()
    {
        var account = await fixture.RegisterOwnerAsync();
        var laptopSid = SidOf(account.AccessToken);
        await fixture.LoginPlainAsync(account);
        var phoneAccess = account.AccessToken;
        var phoneSid = SidOf(phoneAccess);
        Assert.NotEqual(laptopSid, phoneSid);

        var renewed = await RefreshAsync(account.RefreshToken);

        Assert.Equal(phoneSid, SidOf(renewed.Access)); // renovar es la misma sesión
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, renewed.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, phoneAccess)).StatusCode); // el anterior, de la misma sesión, sigue valiendo
    }

    [Fact]
    public async Task AfterAnyNumberOfRenewals_TheSessionAndItsFirstAccessTokenStayValid()
    {
        var account = await fixture.RegisterOwnerAsync();
        var first = account.AccessToken;
        var refresh = account.RefreshToken;

        for (var i = 0; i < 3; i++)
        {
            var renewed = await RefreshAsync(refresh);
            refresh = renewed.Refresh;
            Assert.Equal(SidOf(first), SidOf(renewed.Access));
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, first)).StatusCode);
    }

    // ===== Cerrar la sesión corta el acceso: todas las vías =====

    [Fact]
    public async Task AfterLogout_TheTokenIsA401_ButTheTokenOfTheOtherAccountIsNot()
    {
        var a = await fixture.RegisterOwnerAsync($"Empresa A {Guid.NewGuid():N}");
        var b = await fixture.RegisterOwnerAsync($"Empresa B {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, a.AccessToken)).StatusCode); // control

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken = a.RefreshToken })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, a.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, b.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ReusingARevokedRefreshToken_KillsTheAccessTokensOfEverySessionOfTheAccount()
    {
        var account = await fixture.RegisterOwnerAsync();
        var firstRefresh = account.RefreshToken;
        await fixture.LoginPlainAsync(account);
        var otherDeviceAccess = account.AccessToken;
        var renewed = await RefreshAsync(firstRefresh); // rota el primero: firstRefresh queda revocado
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, renewed.Access)).StatusCode); // control

        // Alguien usa el token ya rotado (una copia robada): la detección revoca TODAS las sesiones activas de la cuenta (ADR-007).
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, RefreshPath, new { refreshToken = firstRefresh })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, renewed.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, otherDeviceAccess)).StatusCode);
    }

    [Fact]
    public async Task DisablingTheSecondFactor_KillsTheAccessTokenOfThatSession()
    {
        var account = await fixture.NewMfaAccountAsync();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, account.AccessToken)).StatusCode); // control

        var disable = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = account.Password, code = fixture.FreshCode(account) }, account.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, account.AccessToken)).StatusCode);
    }

    // ===== Lo que se rechaza sin más: token sin sesión comprobable =====

    [Fact]
    public async Task ATokenWithoutSid_WithASidThatIsNotAGuid_OrWithAnUnknownSid_IsA401()
    {
        var account = await fixture.RegisterOwnerAsync();
        var token = Read(account.AccessToken);
        Claim[] Base() => [new("sub", token.Subject), new("tenant_id", token.Claims.Single(c => c.Type == "tenant_id").Value), new("role", "Owner")];

        // Control: el mismo token con el «sid» de la sesión de verdad vale. Es lo único que cambia entre los casos de abajo.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, Mint([.. Base(), new Claim("sid", SidOf(account.AccessToken))]))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, Mint(Base()))).StatusCode); // los emitidos antes de la spec 015
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, Mint([.. Base(), new Claim("sid", "esto-no-es-un-guid")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, Mint([.. Base(), new Claim("sid", Guid.NewGuid().ToString())]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, Mint([.. Base(), new Claim("sid", Guid.Empty.ToString())]))).StatusCode);
    }

    [Fact]
    public async Task TheSidOfAnotherAccountsSession_DoesNotWorkInMyToken()
    {
        var mine = await fixture.RegisterOwnerAsync($"Empresa mía {Guid.NewGuid():N}");
        var theirs = await fixture.RegisterOwnerAsync($"Empresa suya {Guid.NewGuid():N}");
        var myToken = Read(mine.AccessToken);

        // Un token firmado de verdad, de MI cuenta y MI empresa, pero con el Id de la sesión de OTRA persona.
        var forged = Mint(
            new Claim("sub", myToken.Subject),
            new Claim("tenant_id", myToken.Claims.Single(c => c.Type == "tenant_id").Value),
            new Claim("role", "Owner"),
            new Claim("sid", SidOf(theirs.AccessToken)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, forged)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, theirs.AccessToken)).StatusCode); // la suya sigue entera
    }

    [Theory]
    [InlineData("/api/v1/customers")]
    [InlineData("/api/v1/jobs")]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/account/me")]
    [InlineData("/api/v1/account/mfa")]
    public async Task EveryAuthenticatedRoute_RejectsTheTokenOfAClosedSession(string path)
    {
        var account = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(path, account.AccessToken)).StatusCode); // control: la ruta existe y la sesión vale
        await fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken = account.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(path, account.AccessToken)).StatusCode);
    }

    // ===== Lo que NO cambia =====

    [Fact]
    public async Task ThePlatformSession_NeedsNoSid_AndIsNotAffected()
    {
        var platformToken = await fixture.PlatformTokenAsync(await fixture.CreateOperatorAsync());

        Assert.False(Read(platformToken).Claims.Any(c => c.Type == "sid"));
        Assert.Equal(HttpStatusCode.OK, (await fixture.ProbeAsync(platformToken)).StatusCode);
    }

    [Fact]
    public async Task AnAnonymousRoute_WithATokenOfAClosedSession_IsServedAsAnonymous()
    {
        var account = await fixture.RegisterOwnerAsync();
        var closedAccess = account.AccessToken;
        await fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken = account.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(CustomersPath, closedAccess)).StatusCode); // control: ese token está muerto

        var login = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password }, bearer: closedAccess);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ===== La respuesta y el evento =====

    [Fact]
    public async Task TheRejection_IsA401WithNoBodyAndNoReason_AndLogsEvent1009WithoutTheTokenNorTheSid()
    {
        var account = await fixture.RegisterOwnerAsync();
        var userId = UserIdOf(account.AccessToken);
        var sid = SidOf(account.AccessToken);
        await fixture.SendAsync(HttpMethod.Post, LogoutPath, new { refreshToken = account.RefreshToken });
        Assert.Empty(RejectionsOf(userId)); // cerrar la sesión no es un rechazo: lo es usar el token después

        var response = await GetAsync(CustomersPath, account.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("session", string.Join(";", response.Headers.SelectMany(h => h.Value)), StringComparison.OrdinalIgnoreCase); // el motivo no sale

        var entry = Assert.Single(RejectionsOf(userId));
        Assert.Contains("SessionNotActive", entry.Message);
        Assert.DoesNotContain(account.AccessToken, entry.Message);
        Assert.DoesNotContain(sid, entry.Message);
        Assert.DoesNotContain(sid, string.Join("\n", fixture.Logs.Entries.Select(e => e.Message)));
    }

    [Fact]
    public async Task ATokenWithoutSid_LogsTheOtherReason()
    {
        var account = await fixture.RegisterOwnerAsync();
        var token = Read(account.AccessToken);

        var response = await GetAsync(CustomersPath, Mint(
            new Claim("sub", token.Subject),
            new Claim("tenant_id", token.Claims.Single(c => c.Type == "tenant_id").Value),
            new Claim("role", "Owner")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("MissingSessionId", Assert.Single(RejectionsOf(token.Subject)).Message);
    }

    [Fact]
    public async Task AValidSession_LogsNoRejection()
    {
        var account = await fixture.RegisterOwnerAsync();

        await GetAsync(CustomersPath, account.AccessToken);
        await GetAsync("/api/v1/account/me", account.AccessToken);

        Assert.Empty(RejectionsOf(UserIdOf(account.AccessToken)));
    }

    // ===== La sesión cerrada no impide entrar de nuevo =====

    [Fact]
    public async Task AfterClosingEverything_ANewLoginIsANewSession_AndItsTokenWorks()
    {
        var account = await fixture.RegisterOwnerAsync();
        var oldSid = SidOf(account.AccessToken);
        await fixture.SendAsync(HttpMethod.Delete, "/api/v1/account/sessions", bearer: account.AccessToken);

        await fixture.LoginPlainAsync(account);

        Assert.NotEqual(oldSid, SidOf(account.AccessToken));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(CustomersPath, account.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ARemovedRole_IsStillEnforced_ByTheMembershipCheckAfterTheSessionCheck()
    {
        // Las dos barreras conviven: la sesión (401) y, después, la pertenencia y el rol de la base de datos (403).
        var owner = await fixture.RegisterOwnerAsync();
        var technician = await fixture.InviteAndJoinAsync(owner, TenantRole.Technician);

        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/users", bearer: technician.AccessToken)).StatusCode); // sesión válida, sin permiso
    }
}

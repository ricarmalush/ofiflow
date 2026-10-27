using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La suspensión de una empresa se hace cumplir de verdad (spec 011, bloque 2), con la API entera y SQL Server real y
/// dos empresas. Todavía no hay operador que la provoque (llega en los bloques siguientes): aquí la suspensión se pone
/// directamente en la base de datos, que es lo que hará el operador. Cada test deja la empresa reactivada.
/// </summary>
public sealed class TenantSuspensionEndToEndTests : IAsyncLifetime
{
    private const string Password = "Password123!";

    private SqlServerApiFactory _factory = null!;
    private HttpClient _anonymous = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory();
        await _factory.StartAsync();
        _anonymous = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _anonymous.Dispose();
        await _factory.DisposeAsync();
    }

    // ===== La comprobación de cada petición =====

    [Fact]
    public async Task AMemberOfASuspendedCompany_WithAStillValidToken_IsForbiddenOnTheNextRequest_AndRecoversOnReactivation()
    {
        var a = await RegisterCompanyAsync("Fontanería A");
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync("/api/v1/customers")).StatusCode); // control: antes entra

        await SetSuspendedAsync(a.TenantId, true);
        try
        {
            var denied = await a.Client.GetAsync("/api/v1/customers");

            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("tenant.suspended", await CodeOfAsync(denied));
            // El evento queda con la operación y los Ids, y sin datos personales.
            var entry = Assert.Single(_factory.Logs.Entries, e => e.EventId == SecurityEventIds.TenantSuspendedAccessDenied && e.Message.Contains(a.TenantId.ToString()));
            Assert.DoesNotContain(a.Email, entry.Message);
        }
        finally
        {
            await SetSuspendedAsync(a.TenantId, false);
        }

        // Reactivar devuelve todo a como estaba, con el MISMO token: no se tocó la pertenencia ni se revocó nada.
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync("/api/v1/customers")).StatusCode);
    }

    [Fact]
    public async Task SuspendingOneCompany_DoesNotAffectAnotherOne()
    {
        var a = await RegisterCompanyAsync("Electricidad A");
        var b = await RegisterCompanyAsync("Pintura B");

        await SetSuspendedAsync(a.TenantId, true);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await a.Client.GetAsync("/api/v1/customers")).StatusCode);

            // B sigue operando en todo: sus peticiones, su login y su renovación de sesión.
            Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/v1/customers")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(b.Email, Password)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(b.Tokens.RefreshToken)).StatusCode);
        }
        finally
        {
            await SetSuspendedAsync(a.TenantId, false);
        }
    }

    [Fact]
    public async Task SuspendingACompany_DoesNotChangeItsData()
    {
        var a = await RegisterCompanyAsync("Carpintería A");
        var created = await a.Client.PostAsJsonAsync("/api/v1/customers", new { type = 0, name = "Cliente de A" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var before = await a.Client.GetStringAsync("/api/v1/customers");

        await SetSuspendedAsync(a.TenantId, true);
        await SetSuspendedAsync(a.TenantId, false);

        Assert.Equal(before, await a.Client.GetStringAsync("/api/v1/customers"));
    }

    // ===== El login =====

    [Fact]
    public async Task ASuspendedCompanysMember_LoggingInWithCorrectCredentials_Gets403_AndItNeverCountsAsAFailedAttempt()
    {
        var a = await RegisterCompanyAsync("Cerrajería A");
        await SetSuspendedAsync(a.TenantId, true);
        try
        {
            // Más de cinco intentos con la contraseña CORRECTA: si contaran como fallos, la cuenta se habría bloqueado
            // (5 fallos, 15 minutos) y el sexto daría un 401. Todos dan el mismo 403.
            for (var attempt = 1; attempt <= 7; attempt++)
            {
                var response = await LoginAsync(a.Email, Password);

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("tenant.suspended", await CodeOfAsync(response));
            }
        }
        finally
        {
            await SetSuspendedAsync(a.TenantId, false);
        }

        // Control: la cuenta no quedó bloqueada y no tiene ningún fallo apuntado.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(a.Email, Password)).StatusCode);
        Assert.Equal(0, await FailedAttemptsAsync(a.UserId));
    }

    [Fact]
    public async Task ASuspendedCompanysLogin_WithAWrongPassword_RevealsNothing()
    {
        var suspended = await RegisterCompanyAsync("Albañilería A");
        var active = await RegisterCompanyAsync("Jardinería B");

        await SetSuspendedAsync(suspended.TenantId, true);
        try
        {
            // Sin las credenciales correctas, nadie aprende que la empresa existe ni que está suspendida: la respuesta es
            // idéntica a la de una contraseña errónea de una empresa activa, y a la de un email que no existe.
            var wrongInSuspended = await FingerprintAsync(await LoginAsync(suspended.Email, "incorrecta"));
            var wrongInActive = await FingerprintAsync(await LoginAsync(active.Email, "incorrecta"));
            var unknown = await FingerprintAsync(await LoginAsync($"{Guid.NewGuid():N}@example.com", "incorrecta"));

            Assert.StartsWith("401|", wrongInSuspended);
            Assert.Equal(wrongInActive, wrongInSuspended);
            Assert.Equal(unknown, wrongInSuspended);
        }
        finally
        {
            await SetSuspendedAsync(suspended.TenantId, false);
        }
    }

    // ===== Renovar la sesión =====

    [Fact]
    public async Task ASuspendedCompany_CannotRenewTheSession_ButTheTokenSurvivesAndWorksAfterReactivation()
    {
        var a = await RegisterCompanyAsync("Fontanería A2");

        await SetSuspendedAsync(a.TenantId, true);
        try
        {
            // Como un token caducado: 401 y sin explicación.
            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(a.Tokens.RefreshToken)).StatusCode);
        }
        finally
        {
            await SetSuspendedAsync(a.TenantId, false);
        }

        // No se revocó ni se borró: al reactivar la empresa el mismo refresh token vuelve a servir (y es el primer uso real).
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(a.Tokens.RefreshToken)).StatusCode);
    }

    // ===== Invitaciones y registro =====

    [Fact]
    public async Task NobodyCanAcceptAnInvitationToASuspendedCompany_WithTheSameAnswerAsAnInvalidToken()
    {
        var a = await RegisterCompanyAsync("Electricidad A2");
        var invitationToken = await InviteAsync(a, $"nueva-{Guid.NewGuid():N}@example.com");

        await SetSuspendedAsync(a.TenantId, true);
        try
        {
            var toSuspended = await AcceptAsync(invitationToken, "Persona Nueva");
            // Bien formado pero desconocido: uno con formato imposible lo rechaza antes la validación, que es otra respuesta.
            var invalid = await AcceptAsync(OpaqueToken.Generate().Value, "Persona Nueva");

            // La misma respuesta que con un token que no sirve: quien lo intenta no aprende que la empresa está suspendida.
            Assert.Equal(HttpStatusCode.BadRequest, toSuspended.StatusCode);
            Assert.Equal("invitation.invalid", await CodeOfAsync(toSuspended));
            Assert.Equal(await FingerprintAsync(invalid), await FingerprintAsync(toSuspended));
            // El motivo real solo queda en el log.
            Assert.Contains(_factory.Logs.Entries, e => e.EventId == SecurityEventIds.InvitationRejected && e.Message.Contains("TenantSuspended"));
        }
        finally
        {
            await SetSuspendedAsync(a.TenantId, false);
        }

        // La invitación NO se consumió: reactivada la empresa, se puede aceptar.
        Assert.True((await AcceptAsync(invitationToken, "Persona Nueva")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task RegisteringANewCompany_IsNotAffectedWhileAnotherOneIsSuspended()
    {
        var a = await RegisterCompanyAsync("Cristalería A");

        await SetSuspendedAsync(a.TenantId, true);
        try
        {
            var registered = await PostAnonymousAsync("/api/v1/auth/register",
                new { companyName = "Empresa Nueva", userName = "Ana", email = $"{Guid.NewGuid():N}@example.com", password = Password });

            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        }
        finally
        {
            await SetSuspendedAsync(a.TenantId, false);
        }
    }

    // ===== Ayudas =====

    private async Task<Company> RegisterCompanyAsync(string companyName)
    {
        var email = $"suspension-{Guid.NewGuid():N}@example.com";

        var register = await PostAnonymousAsync("/api/v1/auth/register", new { companyName, userName = "Dueña de pruebas", email, password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var tenantId = (await register.Content.ReadFromJsonAsync<RegisterResponse>())!.TenantId;

        var login = await LoginAsync(email, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthResultDto>())!;

        var userId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken).Subject);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return new Company(tenantId, userId, email, tokens, client);
    }

    private async Task SetSuspendedAsync(Guid tenantId, bool suspended)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        if (suspended)
        {
            tenant.Suspend(DateTime.UtcNow);
        }
        else if (tenant.IsSuspended)
        {
            tenant.Reactivate();
        }

        await db.SaveChangesAsync();
    }

    private async Task<int> FailedAttemptsAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Set<OfiFlow.Infrastructure.Identity.ApplicationUser>()
            .Where(user => user.Id == userId)
            .Select(user => user.AccessFailedCount)
            .SingleAsync();
    }

    private async Task<string> InviteAsync(Company company, string email)
    {
        var response = await company.Client.PostAsJsonAsync("/api/v1/invitations", new { email, role = (int)TenantRole.Technician });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<InvitationCreated>())!.Token;
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        PostAnonymousAsync("/api/v1/auth/login", new { email, password });

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        PostAnonymousAsync("/api/v1/auth/refresh", new { refreshToken });

    private Task<HttpResponseMessage> AcceptAsync(string token, string name) =>
        PostAnonymousAsync("/api/v1/auth/accept-invitation", new { token, name, password = Password });

    /// <summary>Cada petición anónima declara una IP distinta: los límites por IP son otra defensa y aquí no deben intervenir.</summary>
    private Task<HttpResponseMessage> PostAnonymousAsync(string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        return _anonymous.SendAsync(request);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("code").GetString();
    }

    /// <summary>Todo lo que un atacante podría usar para distinguir dos respuestas, sin lo que cambia siempre (hora, tamaño, traceId).</summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Content-Length"))
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}={string.Join(",", header.Value)}");

        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0 && body.TrimStart().StartsWith('{'))
        {
            // El traceId es distinto en cada petición por diseño: no es una diferencia que el atacante pueda aprovechar.
            using var document = JsonDocument.Parse(body);
            body = string.Join(",", document.RootElement.EnumerateObject()
                .Where(property => property.Name != "traceId")
                .Select(property => $"{property.Name}={property.Value}"));
        }

        return $"{(int)response.StatusCode}|{string.Join(";", headers)}|{body}";
    }

    private sealed record RegisterResponse(Guid TenantId);

    private sealed record InvitationCreated(Guid Id, string Token);

    private sealed record Company(Guid TenantId, Guid UserId, string Email, AuthResultDto Tokens, HttpClient Client);
}

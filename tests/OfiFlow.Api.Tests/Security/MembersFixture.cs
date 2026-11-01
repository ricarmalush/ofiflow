using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Una empresa con su Owner (registrado y con login por la API) y miembros de otros roles sembrados en la base de datos,
/// para probar las rutas de invitaciones y miembros (spec 009) sin gastar los 5 logins por minuto que permite la API:
/// los miembros sembrados llevan un token firmado con el secreto de la factoría (el rol del token no autoriza nada,
/// decide la base de datos), y solo el Owner hace login de verdad.
/// </summary>
public sealed class MembersFixture : IAsyncLifetime
{
    public SqlServerApiFactory Factory { get; } = new();

    public HttpClient Anonymous { get; private set; } = null!;

    public HttpClient OwnerClient { get; private set; } = null!;

    public Guid TenantId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    /// <summary>Las credenciales del Owner, para las operaciones que piden la contraseña de nuevo (spec 010).</summary>
    public string OwnerEmail { get; private set; } = string.Empty;

    public string OwnerPassword { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Factory.StartAsync();
        Anonymous = Factory.CreateClient();

        var email = $"miembros-{Guid.NewGuid():N}@example.com";
        var password = NewPassword();
        OwnerEmail = email;
        OwnerPassword = password;

        var register = await Anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new { companyName = "Empresa de miembros", userName = "Dueña de pruebas", email, password });
        TenantId = (await ReadAsync<RegisterResponse>(register)).TenantId;

        var login = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var tokens = await ReadAsync<AuthResultDto>(login);

        OwnerUserId = Guid.Parse(new JsonWebToken(tokens.AccessToken).Claims.Single(claim => claim.Type == "sub").Value);
        OwnerClient = ClientWithToken(tokens.AccessToken);
    }

    public Task DisposeAsync()
    {
        OwnerClient?.Dispose();
        Anonymous?.Dispose();
        return Factory.DisposeAsync().AsTask();
    }

    /// <summary>Un miembro nuevo con el rol indicado, sembrado en la base de datos, y un cliente HTTP con su token.</summary>
    public async Task<Member> SeedMemberAsync(TenantRole role, Guid? tenantId = null)
    {
        var company = tenantId ?? TenantId;
        var email = $"miembro-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var userId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var created = await identity.CreateUserAsync(userId, email, NewPassword(), CancellationToken.None);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"No se pudo sembrar el miembro {role}: {string.Join(", ", created.Errors)}");
        }

        var membership = TenantUser.Create(company, userId, role);
        db.Users.Add(User.Create(userId, $"Miembro {role}", Email.Create(email)));
        db.TenantUsers.Add(membership);
        await db.SaveChangesAsync();

        return new Member(role, userId, membership.Id, email, ClientWithToken(MintToken(userId, company, role.ToString())));
    }

    /// <summary>Cuántas pertenencias de la empresa existen con ese Id (0 o 1), mirando la base de datos directamente.</summary>
    public async Task<int> MembershipCountAsync(Guid tenantUserId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.TenantUsers.IgnoreQueryFilters().CountAsync(tu => tu.Id == tenantUserId);
    }

    public async Task<TenantRole> RoleOfAsync(Guid tenantUserId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return (await db.TenantUsers.IgnoreQueryFilters().SingleAsync(tu => tu.Id == tenantUserId)).Role;
    }

    /// <summary>
    /// Acepta una invitación como lo haría la persona: sin sesión y desde SU propia IP. La API limita las aceptaciones a 10 por
    /// hora y por IP, y todos los clientes de prueba comparten una; cada aceptación declara una IP distinta (ver
    /// <see cref="TestClientIpStartupFilter"/>), de modo que el límite no corta los tests. El límite real se prueba aparte.
    /// </summary>
    public Task<HttpResponseMessage> AcceptInvitationAsync(string token, string name, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/accept-invitation") { Content = JsonContent.Create(new { token, name, password }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        return Anonymous.SendAsync(request);
    }

    /// <summary>Registra OTRA empresa por la API (con su Owner ya autenticado), para probar el aislamiento entre empresas.</summary>
    public async Task<Company> RegisterCompanyAsync(string companyName)
    {
        var email = $"otra-{Guid.NewGuid():N}@example.com";
        var password = NewPassword();

        var register = await Anonymous.PostAsJsonAsync("/api/v1/auth/register", new { companyName, userName = $"Dueña de {companyName}", email, password });
        var tenantId = (await ReadAsync<RegisterResponse>(register)).TenantId;

        var login = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var tokens = await ReadAsync<AuthResultDto>(login);

        return new Company(tenantId, email, ClientWithToken(tokens.AccessToken), password);
    }

    /// <summary>Hace caducar una invitación directamente en la base de datos (no se puede esperar 7 días).</summary>
    public async Task ExpireInvitationAsync(Guid invitationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var invitation = await db.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitationId);
        db.Entry(invitation).Property(nameof(Invitation.ExpiresAt)).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>Crea una cuenta con ese email sin pasar por la API, como si alguien se hubiera registrado entre tanto.</summary>
    public async Task CreateAccountAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var created = await identity.CreateUserAsync(Guid.NewGuid(), email, NewPassword(), CancellationToken.None);
        Assert.True(created.Succeeded);
        await db.SaveChangesAsync();
    }

    public async Task<bool> EmailHasAnAccountAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IIdentityService>().IsEmailRegisteredAsync(email, CancellationToken.None);
    }

    /// <summary>
    /// Todas las columnas de una invitación juntas, como texto, tal como están en la base de datos. Sirve para comprobar que
    /// el token en claro no está en NINGUNA.
    /// </summary>
    public async Task<string> InvitationRowAsTextAsync(Guid invitationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Database
            .SqlQuery<string>($"SELECT CONCAT(Email, '|', NormalizedEmail, '|', [Role], '|', TokenHash, '|', CONVERT(varchar(40), CreatedByUserId)) AS [Value] FROM Invitations WHERE Id = {invitationId}")
            .SingleAsync();
    }

    public sealed record Company(Guid TenantId, string OwnerEmail, HttpClient OwnerClient, string OwnerPassword = "");

    public HttpClient ClientWithToken(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Un token firmado con el secreto de la API. El rol del token no autoriza nada: decide la base de datos. Lleva el «sid» de una sesión
    /// abierta de verdad (spec 015): sin ella la API lo rechazaría con 401.
    /// </summary>
    public string MintToken(Guid userId, Guid tenantId, string role) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Factory.JwtIssuer,
            Audience = Factory.JwtAudience,
            Subject = new ClaimsIdentity([
                new Claim("sub", userId.ToString()),
                new Claim("tenant_id", tenantId.ToString()),
                new Claim("role", role),
                new Claim("sid", TestSessions.OpenSessionId(Factory, userId, tenantId))]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(Factory.JwtSecret)), SecurityAlgorithms.HmacSha256)
        });

    public static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"La preparación del test falló: {(int)response.StatusCode} en {response.RequestMessage?.RequestUri}. {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record RegisterResponse(Guid TenantId);

    public sealed record Member(TenantRole Role, Guid UserId, Guid TenantUserId, string Email, HttpClient Client);
}

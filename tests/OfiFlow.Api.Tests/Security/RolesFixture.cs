using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Una empresa con un usuario de cada rol, listos para hacer peticiones por HTTP (spec 008). El Owner se
/// registra por la API; los demás se siembran directamente en la base de datos, porque todavía no existe forma
/// de invitar usuarios. Todos hacen login de verdad: el token que usan es el que emite la API.
/// El login admite 5 peticiones por minuto, y aquí se usan exactamente 5 (una por rol): los tokens extra que
/// necesiten los tests se fabrican con el secreto de la factoría, sin pasar por el login.
/// </summary>
public sealed class RolesFixture : IAsyncLifetime
{
    private static readonly TenantRole[] Roles =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    private readonly Dictionary<TenantRole, RoleUser> _users = [];

    public SqlServerApiFactory Factory { get; } = new();

    public HttpClient Anonymous { get; private set; } = null!;

    public Guid TenantId { get; private set; }

    /// <summary>Un cliente que pertenece a OTRA empresa, sembrado directamente en la base de datos.</summary>
    public Guid CustomerIdOfAnotherTenant { get; private set; }

    public RoleUser UserOf(TenantRole role) => _users[role];

    public HttpClient ClientOf(TenantRole role) => _users[role].Client;

    public async Task InitializeAsync()
    {
        await Factory.StartAsync();
        Anonymous = Factory.CreateClient();

        // 1. El Owner se registra por la API, como lo haría un usuario real.
        var ownerEmail = NewEmail(TenantRole.Owner);
        var ownerPassword = NewPassword();
        var register = await Anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new { companyName = "Empresa de roles", userName = "Dueño de pruebas", email = ownerEmail, password = ownerPassword });
        TenantId = (await ReadAsync<RegisterResponse>(register)).TenantId;

        // 2. El resto de roles se siembran en la misma empresa.
        var credentials = new Dictionary<TenantRole, (string Email, string Password, Guid TenantUserId)>
        {
            [TenantRole.Owner] = (ownerEmail, ownerPassword, Guid.Empty)
        };
        foreach (var role in Roles.Where(role => role != TenantRole.Owner))
        {
            credentials[role] = await SeedUserAsync(role);
        }

        // 3. Todos hacen login por HTTP y guardan su token real.
        foreach (var role in Roles)
        {
            var (email, password, tenantUserId) = credentials[role];
            var login = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
            var tokens = await ReadAsync<AuthResultDto>(login);

            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            _users[role] = new RoleUser(role, UserIdOf(tokens.AccessToken), tenantUserId, tokens.AccessToken, tokens.RefreshToken, client);
        }

        // 4. Un cliente de otra empresa, para comprobar que una denegación no depende del recurso pedido.
        CustomerIdOfAnotherTenant = await SeedCustomerOfAnotherTenantAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var user in _users.Values)
        {
            user.Client.Dispose();
        }

        Anonymous?.Dispose();
        return Factory.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Fabrica un token firmado con el secreto de la API, con el rol que se indique (o sin ninguno). Sirve para
    /// formas de token que el login nunca emite: sin rol, con un rol que no existe, con otra capitalización.
    /// </summary>
    public string MintToken(Guid userId, Guid tenantId, string? role)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("tenant_id", tenantId.ToString())
        };
        if (role is not null)
        {
            claims.Add(new Claim("role", role));
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Factory.JwtIssuer,
            Audience = Factory.JwtAudience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(Factory.JwtSecret)), SecurityAlgorithms.HmacSha256)
        });
    }

    public HttpClient ClientWithToken(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(string Email, string Password, Guid TenantUserId)> SeedUserAsync(TenantRole role)
    {
        // Datos ficticios y generados en cada ejecución: ni credenciales fijas ni datos personales reales.
        var email = NewEmail(role);
        var password = NewPassword();
        var userId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var created = await identity.CreateUserAsync(userId, email, password, CancellationToken.None);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"No se pudo sembrar el usuario {role}: {string.Join(", ", created.Errors)}");
        }

        var tenantUser = TenantUser.Create(TenantId, userId, role);
        db.Users.Add(User.Create(userId, $"Usuario {role}", Email.Create(email)));
        db.TenantUsers.Add(tenantUser);
        await db.SaveChangesAsync();

        return (email, password, tenantUser.Id);
    }

    private async Task<Guid> SeedCustomerOfAnotherTenantAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var customer = Customer.Create(Guid.NewGuid(), CustomerType.Person, "Cliente de otra empresa", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return customer.Id;
    }

    private static string NewEmail(TenantRole role) => $"rbac-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";

    private static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private static Guid UserIdOf(string accessToken) =>
        Guid.Parse(new JsonWebToken(accessToken).Claims.Single(claim => claim.Type == "sub").Value);

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

    /// <summary>Un usuario de la empresa con su rol, su token real y un cliente HTTP que ya lo lleva puesto.</summary>
    public sealed record RoleUser(TenantRole Role, Guid UserId, Guid TenantUserId, string AccessToken, string RefreshToken, HttpClient Client);
}

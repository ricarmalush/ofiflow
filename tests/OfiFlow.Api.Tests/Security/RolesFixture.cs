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
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Una empresa con un usuario de cada rol, listos para hacer peticiones por HTTP (spec 008). El Owner se
/// registra por la API y los demás roles **nacen por invitación** (spec 009): el Owner invita, cada persona acepta y
/// elige su contraseña. Todos hacen login de verdad: el token que usan es el que emite la API.
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

        // 2. El Owner inicia sesión y, con su token, INVITA al resto de roles; cada persona acepta su invitación y
        //    elige su propia contraseña. Es el flujo real: los cinco roles nacen por la API (spec 009), ya no se siembran.
        var ownerLogin = await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = ownerEmail, password = ownerPassword });
        var ownerTokens = await ReadAsync<AuthResultDto>(ownerLogin);
        using var inviter = ClientWithToken(ownerTokens.AccessToken);

        var passwords = new Dictionary<TenantRole, string> { [TenantRole.Owner] = ownerPassword };
        var emails = new Dictionary<TenantRole, string> { [TenantRole.Owner] = ownerEmail };
        foreach (var role in Roles.Where(role => role != TenantRole.Owner))
        {
            emails[role] = NewEmail(role);
            passwords[role] = NewPassword();

            var invite = await inviter.PostAsJsonAsync("/api/v1/invitations", new { email = emails[role], role = (int)role });
            var invitation = await ReadAsync<InvitationCreatedDto>(invite);

            var accept = await AcceptWithOwnIpAsync(invitation.Token, $"Usuario {role}", passwords[role]);
            await EnsureSuccessAsync(accept, $"aceptar la invitación de {role}");
        }

        // El Id de cada pertenencia (el que usan las operaciones de asignar, cambiar rol o quitar) sale del listado.
        var members = await ReadAsync<List<TenantUserDto>>(await inviter.GetAsync("/api/v1/users"));
        var tenantUserIds = Roles.ToDictionary(role => role, role => members.Single(member => member.Email == emails[role]).Id);

        // 3. Todos hacen login por HTTP y guardan su token real. El del Owner ya está hecho: en total 5 logins, el
        //    máximo por minuto que admite la API.
        foreach (var role in Roles)
        {
            var tokens = role == TenantRole.Owner
                ? ownerTokens
                : await ReadAsync<AuthResultDto>(await Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = emails[role], password = passwords[role] }));

            var client = ClientWithToken(tokens.AccessToken);
            _users[role] = new RoleUser(role, UserIdOf(tokens.AccessToken), tenantUserIds[role], tokens.AccessToken, tokens.RefreshToken, client);
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

    /// <summary>
    /// Un miembro NUEVO de la empresa con el rol indicado, y un token firmado a su nombre (sin pasar por el login, que
    /// tiene límite). Para los tests que modifican al miembro en la base de datos sin tocar a los cinco usuarios
    /// compartidos de la fixture. Se siembra directamente (y no por invitación) porque aceptar tiene un límite de 10
    /// por hora y por IP, y estos miembros se crean a decenas.
    /// </summary>
    public async Task<(Guid UserId, Guid TenantUserId, string Token)> NewMemberAsync(TenantRole role)
    {
        var (_, _, tenantUserId) = await SeedUserAsync(role);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = db.TenantUsers.IgnoreQueryFilters().Where(tu => tu.Id == tenantUserId).Select(tu => tu.UserId).Single();

        return (userId, tenantUserId, MintToken(userId, TenantId, role.ToString()));
    }

    /// <summary>Cambia el rol de un miembro directamente en la base de datos, como lo haría la operación de gestión.</summary>
    public async Task ChangeRoleInDatabaseAsync(Guid tenantUserId, TenantRole role)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.TenantUsers.IgnoreQueryFilters().Single(tu => tu.Id == tenantUserId).ChangeRole(role);
        await db.SaveChangesAsync();
    }

    /// <summary>Quita la pertenencia de un miembro directamente en la base de datos (su token sigue sin caducar).</summary>
    public async Task RemoveMembershipInDatabaseAsync(Guid tenantUserId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.TenantUsers.Remove(db.TenantUsers.IgnoreQueryFilters().Single(tu => tu.Id == tenantUserId));
        await db.SaveChangesAsync();
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

    /// <summary>Acepta una invitación desde una IP propia (ver <see cref="TestClientIpStartupFilter"/>), para no gastar el límite de aceptar.</summary>
    private Task<HttpResponseMessage> AcceptWithOwnIpAsync(string token, string name, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/accept-invitation") { Content = JsonContent.Create(new { token, name, password }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        return Anonymous.SendAsync(request);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string step)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"La preparación del test falló al {step}: {(int)response.StatusCode} en {response.RequestMessage?.RequestUri}. " +
                await response.Content.ReadAsStringAsync());
        }
    }

    private sealed record RegisterResponse(Guid TenantId);

    /// <summary>Un usuario de la empresa con su rol, su token real y un cliente HTTP que ya lo lleva puesto.</summary>
    public sealed record RoleUser(TenantRole Role, Guid UserId, Guid TenantUserId, string AccessToken, string RefreshToken, HttpClient Client);
}

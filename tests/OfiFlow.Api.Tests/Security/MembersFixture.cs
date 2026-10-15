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

    public async Task InitializeAsync()
    {
        await Factory.StartAsync();
        Anonymous = Factory.CreateClient();

        var email = $"miembros-{Guid.NewGuid():N}@example.com";
        var password = NewPassword();

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
    public async Task<Member> SeedMemberAsync(TenantRole role)
    {
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

        var membership = TenantUser.Create(TenantId, userId, role);
        db.Users.Add(User.Create(userId, $"Miembro {role}", Email.Create(email)));
        db.TenantUsers.Add(membership);
        await db.SaveChangesAsync();

        return new Member(role, userId, membership.Id, email, ClientWithToken(MintToken(userId, TenantId, role.ToString())));
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

    public HttpClient ClientWithToken(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Un token firmado con el secreto de la API. El rol del token no autoriza nada: decide la base de datos.</summary>
    public string MintToken(Guid userId, Guid tenantId, string role) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Factory.JwtIssuer,
            Audience = Factory.JwtAudience,
            Subject = new ClaimsIdentity([new Claim("sub", userId.ToString()), new Claim("tenant_id", tenantId.ToString()), new Claim("role", role)]),
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

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Platform;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// SOLO PARA TESTS. Una operación de plataforma mínima: todavía no hay rutas de plataforma con efecto (llegan en el bloque 6) y
/// la sesión de plataforma tiene que probarse de punta a punta con una. Pasa por el pipeline de verdad (AuthorizationBehavior).
/// </summary>
[RequiresPlatformOperator]
public sealed record PlatformProbe : IRequest<string>;

public sealed class PlatformProbeHandler : IRequestHandler<PlatformProbe, string>
{
    public Task<string> Handle(PlatformProbe request, CancellationToken cancellationToken) => Task.FromResult("sesión de plataforma aceptada");
}

/// <summary>SOLO PARA TESTS. Mapea <see cref="PlatformFlowFixture.ProbeRoute"/> con la misma política que usarán las rutas reales.</summary>
public sealed class PlatformProbeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        app.UseEndpoints(endpoints => endpoints
            .MapGet(PlatformFlowFixture.ProbeRoute, async (ISender sender, CancellationToken cancellationToken) =>
                Results.Ok(await sender.Send(new PlatformProbe(), cancellationToken)))
            .RequireAuthorization(PlatformAuthentication.Policy));
    };
}

/// <summary>
/// La API entera con SQL Server real, el reloj controlado del segundo factor y la sesión de plataforma (spec 011, bloque 5): además
/// de la ruta de prueba, un verificador de contraseñas que cuenta y una firma de tokens que sigue el reloj real, como la de las
/// empresas (el reloj controlado solo gobierna el segundo factor).
/// </summary>
public sealed class PlatformFlowFixture : MfaFlowFixture
{
    public const string ProbeRoute = "/api/v1/platform/probe";

    public CountingPasswordChecker Checker { get; } = new();

    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPasswordChecker>(Checker);
            services.AddTransient<IRequestHandler<PlatformProbe, string>, PlatformProbeHandler>();
            services.AddScoped<IPlatformTokenService>(provider => new PlatformTokenService(provider.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System));
            services.AddTransient<IStartupFilter, PlatformProbeStartupFilter>();
        });

    public JwtOptions Jwt => Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    // ===== Operadores (se crean como en producción: con el servicio que solo conoce el programa del servidor) =====

    public sealed class Operator(Guid userId, string email, string password, byte[] secret)
    {
        public Guid UserId { get; } = userId;

        public string Email { get; } = email;

        public string Password { get; } = password;

        public byte[] Secret { get; } = secret;

        public List<string> RecoveryCodes { get; } = [];
    }

    private PlatformOperatorAdministration Administration(IServiceScope scope) =>
        ActivatorUtilities.CreateInstance<PlatformOperatorAdministration>(scope.ServiceProvider);

    public async Task<Operator> CreateOperatorAsync()
    {
        var email = $"operador-{Guid.NewGuid():N}@example.com";
        var password = MembersFixture.NewPassword();
        var secret = RandomNumberGenerator.GetBytes(TotpSecret.ByteLength);

        using var scope = Services.CreateScope();
        var result = await Administration(scope).CreateAsync(email, password, secret, Totp.Compute(secret, Totp.StepAt(Clock.GetUtcNow())), CancellationToken.None);
        Assert.Equal(OperatorCreationOutcome.Created, result.Outcome);

        var platformOperator = new Operator(result.UserId, email, password, secret);
        platformOperator.RecoveryCodes.AddRange(result.RecoveryCodesInClear);
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 2)); // el primer código ya está gastado

        return platformOperator;
    }

    public async Task DisableOperatorAsync(Operator platformOperator)
    {
        using var scope = Services.CreateScope();
        Assert.Equal(OperatorDisableOutcome.Disabled, await Administration(scope).DisableAsync(platformOperator.Email, CancellationToken.None));
    }

    /// <summary>Un código que no se ha usado nunca: se avanza un paso de 30 s y se calcula el de ese instante.</summary>
    public string FreshCode(Operator platformOperator)
    {
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        return Totp.Compute(platformOperator.Secret, Totp.StepAt(Clock.GetUtcNow()));
    }

    // ===== El inicio de sesión de plataforma =====

    public Task<HttpResponseMessage> PlatformLoginAsync(string email, string password) =>
        SendAsync(HttpMethod.Post, "/api/v1/platform/auth/login", new { email, password });

    public Task<HttpResponseMessage> PlatformVerifyAsync(string challenge, string code) =>
        SendAsync(HttpMethod.Post, "/api/v1/platform/auth/verify", new { challenge, code });

    public async Task<string> PlatformChallengeAsync(Operator platformOperator)
    {
        var response = await PlatformLoginAsync(platformOperator.Email, platformOperator.Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonOf(response);
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());

        return body.GetProperty("challenge").GetString()!;
    }

    /// <summary>Los dos pasos con un código válido: devuelve la respuesta completa del segundo paso.</summary>
    public async Task<JsonElement> OpenPlatformSessionAsync(Operator platformOperator)
    {
        var challenge = await PlatformChallengeAsync(platformOperator);
        var response = await PlatformVerifyAsync(challenge, FreshCode(platformOperator));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await JsonOf(response);
    }

    public async Task<string> PlatformTokenAsync(Operator platformOperator) =>
        (await OpenPlatformSessionAsync(platformOperator)).GetProperty("accessToken").GetString()!;

    public Task<HttpResponseMessage> ProbeAsync(string? bearer) => SendAsync(HttpMethod.Get, ProbeRoute, bearer: bearer);

    // ===== Tokens fabricados: formas que el login nunca emite =====

    public string Forge(
        string secretBase64, string audience, Guid userId, bool platformRealm = true, string? tenantId = null, string? role = null, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        if (platformRealm)
        {
            claims.Add(new Claim(OfiFlowClaimTypes.Realm, OfiFlowClaimTypes.PlatformRealm));
        }

        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (role is not null)
        {
            claims.Add(new Claim("role", role));
        }

        var expiresAt = lifetime is { } span ? now + span : now.AddMinutes(10);
        var notBefore = expiresAt < now ? expiresAt.AddMinutes(-5) : now.AddSeconds(-1);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(secretBase64)), SecurityAlgorithms.HmacSha256);

        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(Database.JwtIssuer, audience, claims, notBefore, expiresAt, credentials));
    }

    /// <summary>Un token de plataforma bien firmado, con la clave y la audiencia de la plataforma.</summary>
    public string ForgePlatform(Guid userId, bool platformRealm = true, TimeSpan? lifetime = null) =>
        Forge(Database.PlatformSecret, Jwt.PlatformAudience, userId, platformRealm, lifetime: lifetime);

    /// <summary>Hace una petición con una IP fija, para los tests del límite de peticiones.</summary>
    public Task<HttpResponseMessage> PostFromAsync(string ip, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);

        return Http.SendAsync(request);
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La sesión de plataforma (spec 011 R7): otra clave, otra audiencia, 15 minutos y sin refresh token. La API no arranca si la
/// configuración de la plataforma falta, está mal o repite la de las empresas, y un token de un mundo no vale en el otro.
/// </summary>
public class PlatformAuthenticationTests
{
    private static readonly string CompanySecret = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string PlatformSecret = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());

    private static JwtOptions Options(Action<JwtOptions>? change = null)
    {
        var options = new JwtOptions
        {
            Secret = CompanySecret,
            Issuer = "OfiFlow.Tests",
            Audience = "OfiFlow.Tests.Client",
            PlatformSecret = PlatformSecret,
            PlatformAudience = "OfiFlow.Tests.Platform"
        };
        change?.Invoke(options);

        return options;
    }

    // ===== La configuración de arranque =====

    [Fact]
    public void AValidConfiguration_YieldsThe32ByteKey()
    {
        var key = PlatformAuthentication.ParseKey(Options());

        Assert.Equal(32, key.Length);
        Assert.Equal(Convert.FromBase64String(PlatformSecret), key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingKey_StopsTheStartup_SayingWhatIsMissing(string? secret)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PlatformAuthentication.ParseKey(Options(o => o.PlatformSecret = secret!)));

        Assert.Contains("Jwt:PlatformSecret", exception.Message);
        Assert.Contains("Falta", exception.Message);
        Assert.Contains("Jwt__PlatformSecret", exception.Message);
    }

    [Fact]
    public void AKeyThatIsNotBase64_StopsTheStartup_WithoutRevealingIt()
    {
        const string notBase64 = "esto-no-es-base64-valido!!!";

        var exception = Assert.Throws<InvalidOperationException>(() => PlatformAuthentication.ParseKey(Options(o => o.PlatformSecret = notBase64)));

        Assert.Contains("Base64", exception.Message);
        Assert.DoesNotContain(notBase64, exception.Message);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void AKeyOfTheWrongLength_StopsTheStartup(int bytes)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

        var exception = Assert.Throws<InvalidOperationException>(() => PlatformAuthentication.ParseKey(Options(o => o.PlatformSecret = secret)));

        Assert.Contains($"{bytes} bytes", exception.Message);
        Assert.DoesNotContain(secret, exception.Message);
    }

    [Fact]
    public void TheSameKeyAsTheCompanies_StopsTheStartup()
    {
        // Dos mundos distintos: con la misma clave, un token de empresa con la audiencia cambiada valdría en la plataforma.
        var exception = Assert.Throws<InvalidOperationException>(() => PlatformAuthentication.ParseKey(Options(o => o.PlatformSecret = o.Secret)));

        Assert.Contains("igual que 'Jwt:Secret'", exception.Message);
    }

    [Fact]
    public void TheSameKeyWrittenDifferently_IsStillTheSameKey()
    {
        // La misma clave con otra forma de escribirla en Base64 (relleno distinto, mayúsculas del alfabeto estándar...) sigue siendo la misma.
        var bytes = Convert.FromBase64String(CompanySecret);
        var withWhitespace = " " + CompanySecret + " ";

        // El decodificador de .NET tolera espacios alrededor: son los mismos 32 bytes.
        Assert.Equal(bytes, Convert.FromBase64String(withWhitespace));

        var exception = Assert.Throws<InvalidOperationException>(() => PlatformAuthentication.ParseKey(Options(o => o.PlatformSecret = withWhitespace)));

        Assert.Contains("igual que 'Jwt:Secret'", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OfiFlow.Tests.Client")] // la misma que la de las empresas
    public void AnAudienceMissingOrEqualToTheCompaniesOne_StopsTheStartup(string? audience)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PlatformAuthentication.ParseKey(Options(o => o.PlatformAudience = audience!)));

        Assert.Contains("Jwt:PlatformAudience", exception.Message);
    }

    [Fact]
    public void TheDefaultPlatformAudience_IsNotTheDefaultOfTheCompanies()
    {
        Assert.NotEqual(new JwtOptions().Audience, new JwtOptions().PlatformAudience);
        Assert.False(string.IsNullOrWhiteSpace(new JwtOptions().PlatformAudience));
    }

    // ===== El registro: esquema y política =====

    private static ServiceProvider Register(Action<Dictionary<string, string?>>? change = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = CompanySecret,
            ["Jwt:PlatformSecret"] = PlatformSecret,
            ["Jwt:Issuer"] = "OfiFlow.Tests",
            ["Jwt:Audience"] = "OfiFlow.Tests.Client",
            ["Jwt:PlatformAudience"] = "OfiFlow.Tests.Platform",
            ["Mfa:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["ConnectionStrings:Default"] = "Server=no-se-usa;Database=no-se-usa"
        };
        change?.Invoke(settings);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheApi_DoesNotStartWithoutThePlatformKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Register(s => s.Remove("Jwt:PlatformSecret")));

        Assert.Contains("Jwt:PlatformSecret", exception.Message);
    }

    [Fact]
    public async Task ThePlatformSchemeExists_AndIsNotTheDefaultOne()
    {
        await using var provider = Register();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var platform = await schemes.GetSchemeAsync(PlatformAuthentication.Scheme);
        var defaultScheme = await schemes.GetDefaultAuthenticateSchemeAsync();

        Assert.NotNull(platform);
        Assert.NotEqual(PlatformAuthentication.Scheme, defaultScheme?.Name); // lo que no declara esquema sigue siendo de empresa
    }

    [Fact]
    public async Task ThePlatformPolicy_OnlyAcceptsThePlatformScheme_AndTheRealmClaim()
    {
        await using var provider = Register();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(PlatformAuthentication.Policy);

        Assert.NotNull(policy);
        Assert.Equal([PlatformAuthentication.Scheme], policy.AuthenticationSchemes);
        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
        Assert.Contains(policy.Requirements, requirement => requirement is ClaimsAuthorizationRequirement claim
            && claim.ClaimType == OfiFlowClaimTypes.Realm
            && claim.AllowedValues!.SequenceEqual([OfiFlowClaimTypes.PlatformRealm]));
    }

    // ===== La sesión que se emite =====

    private static PlatformTokenService Service(FakeTimeProvider clock) =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), clock);

    private static TokenValidationParameters Parameters(string secret, string audience, TimeProvider? clock = null) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "OfiFlow.Tests",
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(secret)),
        ValidateLifetime = true,
        // La versión de IdentityModel no acepta un reloj: el validador de vida usa el del test.
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            return notBefore <= now && now < expires;
        },
        ClockSkew = TimeSpan.Zero
    };

    [Fact]
    public void TheSession_ValidatesWithThePlatformKeyAndAudience_AndCarriesOnlyTheAccountAndTheRealm()
    {
        var clock = new FakeTimeProvider(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));
        var operatorId = Guid.NewGuid();

        var session = Service(clock).IssuePlatformSession(operatorId);

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }
            .ValidateToken(session.AccessToken, Parameters(PlatformSecret, "OfiFlow.Tests.Platform", clock), out _);

        Assert.Equal(operatorId.ToString(), principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal(OfiFlowClaimTypes.PlatformRealm, principal.FindFirst(OfiFlowClaimTypes.Realm)?.Value);

        // Un operador no tiene empresa ni rol: ninguno de esos claims viaja.
        Assert.Null(principal.FindFirst(OfiFlowClaimTypes.TenantId));
        Assert.Null(principal.FindFirst(OfiFlowClaimTypes.Role));
    }

    [Fact]
    public void TheSession_LastsExactlyFifteenMinutes()
    {
        var clock = new FakeTimeProvider(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));

        var session = Service(clock).IssuePlatformSession(Guid.NewGuid());

        Assert.Equal(TimeSpan.FromMinutes(15), PlatformAuthentication.SessionLifetime);
        Assert.Equal(new DateTime(2026, 10, 26, 12, 15, 0, DateTimeKind.Utc), session.ExpiresAt);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken);
        Assert.Equal(session.ExpiresAt, token.ValidTo);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, token.ValidFrom);
    }

    [Fact]
    public void TheSession_HasNoRefreshToken()
    {
        // R7: pasados los 15 minutos hay que repetir contraseña y código. El resultado no tiene ni dónde llevar uno.
        var members = typeof(OfiFlow.Application.Platform.PlatformSessionDto).GetProperties().Select(property => property.Name).Order().ToList();

        Assert.Equal(["AccessToken", "ExpiresAt"], members);
    }

    [Fact]
    public void ThePlatformSession_DoesNotValidateAsACompanySession()
    {
        // La firma es correcta en su mundo, pero ni la clave ni la audiencia de las empresas la aceptan (R7).
        var session = Service(new FakeTimeProvider(DateTime.UtcNow)).IssuePlatformSession(Guid.NewGuid());
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() =>
            handler.ValidateToken(session.AccessToken, Parameters(CompanySecret, "OfiFlow.Tests.Client"), out _));
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(session.AccessToken, Parameters(PlatformSecret, "OfiFlow.Tests.Client"), out _)); // la clave buena con la audiencia de empresa
    }

    [Fact]
    public void ACompanyToken_DoesNotValidateAsAPlatformSession()
    {
        // El camino inverso: un token firmado con la clave de empresa no vale con la clave ni con la audiencia de plataforma.
        var key = new SymmetricSecurityKey(Convert.FromBase64String(CompanySecret));
        var companyToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "OfiFlow.Tests", "OfiFlow.Tests.Platform", [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(companyToken, Parameters(PlatformSecret, "OfiFlow.Tests.Platform"), out _));
    }

    [Fact]
    public void EverySession_IsDifferent()
    {
        var service = Service(new FakeTimeProvider(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc)));
        var operatorId = Guid.NewGuid();

        var first = service.IssuePlatformSession(operatorId).AccessToken;
        var second = service.IssuePlatformSession(operatorId).AccessToken;

        Assert.NotEqual(first, second); // mismo reloj, misma cuenta: el identificador del token las distingue
    }
}

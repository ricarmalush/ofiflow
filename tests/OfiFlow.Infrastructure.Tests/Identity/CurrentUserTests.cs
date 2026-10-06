using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>ADR-012 R4: quién hace la petición, leído del token. Sin Docker.</summary>
public class CurrentUserTests
{
    private static CurrentUser For(params Claim[] claims)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };

        return new CurrentUser(accessor);
    }

    [Fact]
    public void ReadsTheUserTheTenantAndTheRole()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var user = For(
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("role", "Manager"));

        Assert.Equal(userId, user.UserId);
        Assert.Equal(tenantId, user.TenantId);
        Assert.Equal(TenantRole.Manager, user.Role);
    }

    [Fact]
    public void WithoutClaims_EverythingIsNull()
    {
        var user = For();

        Assert.Null(user.UserId);
        Assert.Null(user.TenantId);
        Assert.Null(user.Role);
    }

    [Fact]
    public void WithoutAnHttpContext_EverythingIsNull()
    {
        var user = new CurrentUser(new HttpContextAccessor());

        Assert.Null(user.UserId);
        Assert.Null(user.TenantId);
        Assert.Null(user.Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]            // un número: Enum.TryParse lo aceptaría como Admin
    [InlineData("999")]
    [InlineData("owner")]        // otra capitalización: TokenService nunca la emite
    [InlineData(" Owner")]
    [InlineData("Superadmin")]
    [InlineData("Owner,Admin")]
    public void ARoleThatIsNotTheExactNameOfARole_IsNull(string value)
    {
        var user = For(new Claim("role", value));

        Assert.Null(user.Role);
    }

    [Fact]
    public void TheRoleIsReadFromTheClaimNamedRoleOnly()
    {
        // El tipo largo (ClaimTypes.Role) no es el que escribe TokenService: no se acepta por si acaso.
        var user = For(new Claim(ClaimTypes.Role, "Owner"));

        Assert.Null(user.Role);
    }

    [Fact]
    public void AMalformedUserOrTenantId_IsNull()
    {
        var user = For(new Claim(JwtRegisteredClaimNames.Sub, "no-es-un-guid"), new Claim("tenant_id", ""));

        Assert.Null(user.UserId);
        Assert.Null(user.TenantId);
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task ATokenIssuedByTokenService_AndValidatedWithTheProductionJwtBearerConfig_IsReadBackWithItsRole(TenantRole role)
    {
        // El riesgo que ADR-012 pide verificar con un token real. Se usa la configuración REAL de JwtBearer
        // (la que registra AddInfrastructure), no un manejador de JWT suelto: JwtBearer renombra por defecto
        // los claims al validar (role -> ClaimTypes.Role), y una versión anterior de este test, que validaba
        // con un manejador a secas, no lo habría detectado.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()),
            ["Jwt:Issuer"] = "OfiFlow.Tests",
            ["Jwt:Audience"] = "OfiFlow.Tests",
            ["ConnectionStrings:Default"] = "Server=no-se-usa;Database=no-se-usa"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var jwtOptions = provider.GetRequiredService<IOptions<JwtOptions>>();
        var bearer = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new FixedTenantContext(Guid.NewGuid()));
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var issued = await new TokenService(db, jwtOptions, NullLogger<TokenService>.Instance)
            .IssueTokensAsync(userId, tenantId, role, CancellationToken.None);

        var handler = bearer.TokenHandlers.OfType<JsonWebTokenHandler>().Single();
        var validation = await handler.ValidateTokenAsync(issued.AccessToken, bearer.TokenValidationParameters);
        Assert.True(validation.IsValid, validation.Exception?.Message);

        var user = For([.. validation.ClaimsIdentity.Claims]);
        Assert.Equal(role, user.Role);
        Assert.Equal(userId, user.UserId);
        Assert.Equal(tenantId, user.TenantId);
    }
}

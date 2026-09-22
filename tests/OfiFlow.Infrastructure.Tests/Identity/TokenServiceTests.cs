using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

public class TokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    private static (ApplicationDbContext Db, TokenService Service) CreateSut()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ApplicationDbContext(dbOptions, new FixedTenantContext(Guid.NewGuid()));
        var service = new TokenService(db, Microsoft.Extensions.Options.Options.Create(Options));

        return (db, service);
    }

    [Fact]
    public async Task IssueTokensAsync_ReturnsAccessTokenWithTenantClaim()
    {
        var (_, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var result = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal(tenantId.ToString(), jwt.Claims.First(c => c.Type == "tenant_id").Value);
        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
    }

    [Fact]
    public async Task RotateRefreshTokenAsync_WithValidToken_ReturnsNewTokensAndRevokesTheOld()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // RotateRefreshTokenAsync relee el rol en TenantUsers en cada refresh (defensa en
        // profundidad: el usuario podría haber dejado de pertenecer al tenant) — hace falta
        // esta fila para que la rotación encuentre membresía válida.
        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var rotated = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.NotEqual(issued.RefreshToken, rotated!.RefreshToken);

        var allTokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, allTokens.Count);
        Assert.Contains(allTokens, t => t.RevokedAt != null);
    }

    [Fact]
    public async Task RotateRefreshTokenAsync_WithAlreadyRevokedToken_RevokesWholeChainAndReturnsNull()
    {
        var (db, service) = CreateSut();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.TenantUsers.Add(TenantUser.CreateOwner(tenantId, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await service.IssueTokensAsync(userId, tenantId, TenantRole.Owner, CancellationToken.None);

        var firstRotation = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);
        Assert.NotNull(firstRotation);

        // Reutilizar el token original (ya revocado) simula un token robado.
        var reuseAttempt = await service.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None);

        Assert.Null(reuseAttempt);

        var allTokens = await db.RefreshTokens.ToListAsync();
        Assert.All(allTokens, t => Assert.NotNull(t.RevokedAt));
    }
}

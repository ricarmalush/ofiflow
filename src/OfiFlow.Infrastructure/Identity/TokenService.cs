using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

public sealed class TokenService(ApplicationDbContext dbContext, IOptions<JwtOptions> jwtOptions) : ITokenService
{
    private readonly JwtOptions _options = jwtOptions.Value;

    public async Task<AuthResultDto> IssueTokensAsync(Guid userId, Guid tenantId, TenantRole role, CancellationToken cancellationToken)
    {
        var accessToken = GenerateAccessToken(userId, tenantId, role);
        var (refreshTokenValue, refreshToken) = CreateRefreshToken(userId, tenantId);

        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResultDto(accessToken, refreshTokenValue, DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes));
    }

    public async Task<AuthResultDto?> RotateRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(refreshToken);

        var existing = await dbContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.RevokedAt is not null)
        {
            // Reutilización de un token ya revocado: posible robo — se revoca toda la
            // cadena activa del usuario (ADR-007).
            var activeTokens = await dbContext.RefreshTokens
                .Where(rt => rt.UserId == existing.UserId && rt.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var token in activeTokens)
            {
                token.Revoke();
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (!existing.IsActive)
        {
            return null;
        }

        // El rol puede haber cambiado desde que se emitió este token; se relee.
        var tenantUser = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(tu => tu.UserId == existing.UserId && tu.TenantId == existing.TenantId, cancellationToken);

        if (tenantUser is null)
        {
            return null;
        }

        existing.Revoke();

        var accessToken = GenerateAccessToken(existing.UserId, existing.TenantId, tenantUser.Role);
        var (newRefreshTokenValue, newRefreshToken) = CreateRefreshToken(existing.UserId, existing.TenantId);

        dbContext.RefreshTokens.Add(newRefreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResultDto(accessToken, newRefreshTokenValue, DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes));
    }

    private string GenerateAccessToken(Guid userId, Guid tenantId, TenantRole role)
    {
        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, role.ToString())
        ];

        var key = new SymmetricSecurityKey(Convert.FromBase64String(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private (string Value, RefreshToken Entity) CreateRefreshToken(Guid userId, Guid tenantId)
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var entity = new RefreshToken(userId, tenantId, Hash(value), DateTime.UtcNow.AddDays(_options.RefreshTokenDays));

        return (value, entity);
    }

    private static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

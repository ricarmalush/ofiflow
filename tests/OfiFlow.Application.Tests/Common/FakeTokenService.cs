using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Common;

public sealed class FakeTokenService : ITokenService
{
    public const string ValidRefreshToken = "valid-refresh-token";

    /// <summary>Cuántas veces se emitió una sesión nueva: con un segundo factor pendiente debe ser cero (spec 010 R12).</summary>
    public int IssuedSessions { get; private set; }

    /// <summary>La cuenta y la empresa de cada sesión emitida, en orden.</summary>
    public List<(Guid UserId, Guid TenantId, TenantRole Role)> Issued { get; } = [];

    public Task<AuthResultDto> IssueTokensAsync(Guid userId, Guid tenantId, TenantRole role, CancellationToken cancellationToken)
    {
        IssuedSessions++;
        Issued.Add((userId, tenantId, role));

        return Task.FromResult(new AuthResultDto($"access-{userId}", ValidRefreshToken, DateTime.UtcNow.AddMinutes(30)));
    }

    /// <summary>Ids de los usuarios cuyos refresh tokens se han borrado.</summary>
    public List<Guid> UsersWithDeletedTokens { get; } = [];

    public Task DeleteRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        UsersWithDeletedTokens.Add(userId);
        return Task.CompletedTask;
    }

    /// <summary>Los refresh tokens que se pidió cerrar, en orden.</summary>
    public List<string> RevokedRefreshTokens { get; } = [];

    public Task<bool> RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        RevokedRefreshTokens.Add(refreshToken);
        return Task.FromResult(refreshToken == ValidRefreshToken);
    }

    public Task<AuthResultDto?> RotateRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(refreshToken == ValidRefreshToken
            ? new AuthResultDto("new-access-token", "new-refresh-token", DateTime.UtcNow.AddMinutes(30))
            : null);
}

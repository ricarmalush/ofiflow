using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Common;

public sealed class FakeTokenService : ITokenService
{
    public const string ValidRefreshToken = "valid-refresh-token";

    public Task<AuthResultDto> IssueTokensAsync(Guid userId, Guid tenantId, TenantRole role, CancellationToken cancellationToken) =>
        Task.FromResult(new AuthResultDto($"access-{userId}", ValidRefreshToken, DateTime.UtcNow.AddMinutes(30)));

    /// <summary>Ids de los usuarios cuyos refresh tokens se han borrado.</summary>
    public List<Guid> UsersWithDeletedTokens { get; } = [];

    public Task DeleteRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        UsersWithDeletedTokens.Add(userId);
        return Task.CompletedTask;
    }

    public Task<AuthResultDto?> RotateRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.FromResult(refreshToken == ValidRefreshToken
            ? new AuthResultDto("new-access-token", "new-refresh-token", DateTime.UtcNow.AddMinutes(30))
            : null);
}

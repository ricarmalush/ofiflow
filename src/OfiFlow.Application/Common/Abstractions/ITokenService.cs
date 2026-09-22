using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Issues and rotates Access/Refresh tokens — implemented in Infrastructure (ADR-007).
/// </summary>
public interface ITokenService
{
    Task<AuthResultDto> IssueTokensAsync(Guid userId, Guid tenantId, TenantRole role, CancellationToken cancellationToken);

    /// <returns>
    /// The new token pair, or null if the refresh token is invalid/expired. If a token that
    /// was already used/revoked is presented again, the whole chain for that user is revoked
    /// (theft detection, ADR-007) and null is returned.
    /// </returns>
    Task<AuthResultDto?> RotateRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
}

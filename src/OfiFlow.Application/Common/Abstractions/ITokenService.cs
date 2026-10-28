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

    /// <summary>
    /// Cierra la sesión de ese refresh token: lo revoca (no lo borra, para que si alguien lo usa después se detecte como
    /// reutilización, ADR-007). Solo toca esa fila. Un token desconocido, caducado o ya revocado no cambia nada y tampoco
    /// revoca la cadena: aquí presentarlo es un cierre, no un uso (spec 013 R3).
    /// </summary>
    /// <returns>true si había una sesión activa y se cerró; false en cualquier otro caso (el llamante no debe distinguirlos hacia fuera).</returns>
    Task<bool> RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>
    /// Borra todos los refresh tokens de un usuario, también los ya revocados, para que no pueda renovar la
    /// sesión ni queden filas huérfanas (spec 009 R14b). No confirma el cambio: lo hace el Handler en su
    /// único SaveChanges, junto con el resto del borrado.
    /// </summary>
    Task DeleteRefreshTokensAsync(Guid userId, CancellationToken cancellationToken);
}

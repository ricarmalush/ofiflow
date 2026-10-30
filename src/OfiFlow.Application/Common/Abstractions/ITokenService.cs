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
    /// Cierra <b>todas</b> las sesiones activas de una cuenta (spec 014): revoca (no borra, como en la spec 013) cada refresh token
    /// activo de ese usuario, en todas sus empresas. No toca los ya revocados ni los caducados, ni los de otra cuenta. Confirma el
    /// cambio por sí mismo y deja el evento <c>1008</c> con el recuento, también si es cero.
    /// </summary>
    /// <returns>Cuántas sesiones activas se cerraron (cero si no había ninguna).</returns>
    Task<int> RevokeAllRefreshTokensAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// ¿Sigue activa esta sesión? (spec 015). Lo está si existe un refresh token <b>de esa sesión y de esa cuenta</b> que no esté revocado
    /// ni caducado. Es lo que la API comprueba en cada petición de empresa para aceptar un access token: cerrar la sesión (por cualquier
    /// vía) lo deja sin refresh token activo y el access token deja de valer al instante. Solo lee: no cambia nada.
    /// </summary>
    /// <remarks>El <paramref name="userId"/> es parte de la pregunta, no un adorno: sin él, el Id de sesión de una cuenta serviría en el token de otra.</remarks>
    Task<bool> IsSessionActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Borra todos los refresh tokens de un usuario, también los ya revocados, para que no pueda renovar la
    /// sesión ni queden filas huérfanas (spec 009 R14b). No confirma el cambio: lo hace el Handler en su
    /// único SaveChanges, junto con el resto del borrado.
    /// </summary>
    Task DeleteRefreshTokensAsync(Guid userId, CancellationToken cancellationToken);
}

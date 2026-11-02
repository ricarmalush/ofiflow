using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Los tokens de restablecimiento de contraseña (spec 018, ADR-021). <b>Este es el único fichero que lee la tabla</b> (como <see cref="TokenService"/> con los
/// refresh tokens): un test de arquitectura lo vigila. Los tokens se buscan por su hash, que es secreto y único, y no tienen filtro de empresa porque son de una
/// cuenta (R17).
/// </summary>
/// <remarks>
/// Por ahora (bloque 1) solo hace dos cosas: la <b>purga</b> de los caducados (R18) y el <b>borrado al dar de baja a una cuenta</b>. Pedir y consumir un
/// token llegan en los bloques 2 y 3, en este mismo fichero.
/// </remarks>
internal sealed class PasswordResetService(ApplicationDbContext dbContext) : IPasswordResetTokenPurger
{
    public Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken) =>
        // Set-based y en LINQ (no SQL a mano: ADR-009 R1). Lo MÁS antiguo primero y con tope de lote. Solo mira ExpiresAt (índice IX_PasswordResetTokens_ExpiresAt):
        // usado o no da igual, porque un token caducado hace más del margen ya no puede usarse. Un token vigente no ha caducado, así que nunca cumple esta condición.
        dbContext.PasswordResetTokens
            .Where(t => t.ExpiresAt < cutoff)
            .OrderBy(t => t.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Borra todos los tokens de una cuenta que se da de baja (spec 009 R14, como <c>MfaStore.RemoveAllAsync</c>): la tabla no tiene clave foránea hacia la
    /// cuenta, así que no se borrarían solos y quedarían filas huérfanas. <b>No confirma el cambio</b>: lo hace el Handler en su único <c>SaveChangesAsync</c>, junto con
    /// el resto de la baja, para que sea todo o nada.
    /// </summary>
    internal static async Task RemoveAllAsync(ApplicationDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.PasswordResetTokens
            .Where(t => t.UserId == userId)
            .ToListAsync(cancellationToken);

        dbContext.PasswordResetTokens.RemoveRange(tokens);
    }
}

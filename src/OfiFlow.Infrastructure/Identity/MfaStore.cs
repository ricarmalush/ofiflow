using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Lo que se hace con las tres tablas del segundo factor en más de un sitio: borrarlas por cuenta. Lo usan
/// <see cref="IdentityService.DeleteUserAsync"/> (la baja de una cuenta, spec 010 R20) y <see cref="MfaService"/>
/// (desactivar o reiniciar el MFA), para que las tres tablas se tratan siempre juntas y ninguna se olvide.
/// </summary>
internal static class MfaStore
{
    /// <summary>
    /// Marca para borrar el secreto, los códigos de recuperación y los desafíos de la cuenta. No confirma: lo hace el
    /// Handler en su único SaveChanges, junto con el resto de la operación. Ninguna de las tres tablas tiene clave
    /// foránea hacia la cuenta (como <see cref="RefreshToken"/>), así que no se borrarían solas.
    /// </summary>
    public static async Task RemoveAllAsync(ApplicationDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        db.MfaCredentials.RemoveRange(await db.MfaCredentials.Where(c => c.UserId == userId).ToListAsync(cancellationToken));
        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken));
        db.MfaChallenges.RemoveRange(await db.MfaChallenges.Where(c => c.UserId == userId).ToListAsync(cancellationToken));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>El resultado de intentar reservar un intento de autenticación.</summary>
/// <param name="Reserved"><c>true</c> si el intento se reservó y hay que comprobar la contraseña; <c>false</c> si la cuenta está bloqueada.</param>
/// <param name="FailedCount">El contador tras reservar (solo significa algo si se reservó).</param>
/// <param name="LockApplied"><c>true</c> si este intento es el que ha llevado el contador al umbral y, por tanto, ha bloqueado la cuenta.</param>
internal readonly record struct AttemptReservation(bool Reserved, int FailedCount, bool LockApplied);

/// <summary>
/// El contador de intentos fallidos y el bloqueo de una cuenta (ADR-014, spec 010 R15-R18). Reutiliza las columnas
/// <c>AccessFailedCount</c> y <c>LockoutEnd</c> que Identity ya tiene. Lo importante es <b>cómo</b> se cuenta:
/// <list type="bullet">
/// <item>El intento se <b>reserva antes de comprobar la contraseña</b>, con una sola sentencia atómica que comprueba
/// que la cuenta no está bloqueada y suma, todo a la vez. Con "leer, comprobar la contraseña y sumar si falla"
/// (lo que hace <c>UserManager</c>), 50 peticiones simultáneas leen "0 fallos", las 50 se comprueban y el bloqueo
/// llega tarde: el atacante obtiene 50 intentos en vez de 5.</item>
/// <item>Al sumar el intento que alcanza el umbral, la <b>misma sentencia</b> fija la hora de fin del bloqueo: no hay
/// un hueco entre "llegó a 5" y "quedó bloqueada".</item>
/// <item>Si el bloqueo ya terminó, la misma sentencia empieza a contar desde cero (el contador "vuelve a cero cuando
/// el bloqueo termina").</item>
/// </list>
/// Se ignora la columna <c>LockoutEnabled</c>: el bloqueo vale para todas las cuentas. Una cuenta con esa columna a
/// <c>false</c> (el valor por defecto de <c>IdentityUser</c>) saldría sin bloqueo: un valor por defecto inseguro.
/// </summary>
internal sealed class AccountLockout(ApplicationDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>
    /// Reserva un intento. Es la única sentencia SQL del proyecto fuera de LINQ: ni <c>ExecuteUpdate</c> ni el
    /// seguimiento de cambios de EF pueden expresar "comprobar y sumar a la vez" devolviendo además el nuevo contador.
    /// Va <b>parametrizada</b> (interpolación de <see cref="FormattableString"/>: cada valor es un parámetro SQL, nunca
    /// texto pegado), solo toca una fila por su Id y no recibe nada del cliente.
    /// </summary>
    public async Task<AttemptReservation> TryReserveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lockedUntil = now + AuthenticationPolicy.LockoutDuration;
        var threshold = AuthenticationPolicy.LockoutThreshold;

        // Todas las partes derechas del SET ven los valores de ANTES de la sentencia. "Bloqueo vencido" =
        // LockoutEnd <= ahora: entonces el intento cuenta como el primero (1) y se borra la hora vieja.
        var rows = await dbContext.Database
            .SqlQuery<int>($"""
                UPDATE ApplicationUsers
                SET AccessFailedCount = CASE WHEN LockoutEnd IS NOT NULL AND LockoutEnd <= {now}
                                             THEN 1 ELSE AccessFailedCount + 1 END,
                    LockoutEnd = CASE
                        WHEN (CASE WHEN LockoutEnd IS NOT NULL AND LockoutEnd <= {now}
                                   THEN 1 ELSE AccessFailedCount + 1 END) >= {threshold} THEN {lockedUntil}
                        WHEN LockoutEnd IS NOT NULL AND LockoutEnd <= {now} THEN NULL
                        ELSE LockoutEnd END
                OUTPUT inserted.AccessFailedCount AS Value
                WHERE Id = {userId}
                  AND (LockoutEnd IS NULL OR LockoutEnd <= {now})
                  AND (AccessFailedCount < {threshold} OR LockoutEnd <= {now})
                """)
            .ToListAsync(cancellationToken); // ToList y no Single: la sentencia no es componible (EF no puede envolverla en una subconsulta)

        // Ninguna fila: la cuenta no existe o está bloqueada (o ya agotó sus intentos y el bloqueo está por fijarse).
        if (rows.Count == 0)
        {
            return new AttemptReservation(Reserved: false, FailedCount: 0, LockApplied: false);
        }

        return new AttemptReservation(Reserved: true, FailedCount: rows[0], LockApplied: rows[0] >= threshold);
    }

    /// <summary>
    /// Devuelve la reserva de un intento que salió bien pero al que le falta otro paso (contraseña correcta en una
    /// cuenta con segundo factor, R18), sin bajar de cero. No toca la hora de bloqueo.
    /// </summary>
    public Task ReleaseAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.ApplicationUsers
            .Where(user => user.Id == userId && user.AccessFailedCount > 0)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.AccessFailedCount, user => user.AccessFailedCount - 1), cancellationToken);

    /// <summary>
    /// Pone el contador a cero y quita el bloqueo: solo con un acceso <b>completo</b> (R17). Una contraseña correcta
    /// con un segundo factor pendiente NO llama a esto, para que quien conoce la contraseña no pueda reiniciar el
    /// contador y seguir adivinando códigos.
    /// </summary>
    public Task ClearAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.ApplicationUsers
            .Where(user => user.Id == userId && (user.AccessFailedCount != 0 || user.LockoutEnd != null))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.AccessFailedCount, 0)
                    .SetProperty(user => user.LockoutEnd, (DateTimeOffset?)null),
                cancellationToken);
}

/// <summary>
/// Los dos eventos del bloqueo (ADR-014), compartidos por todo lo que cuenta intentos: el login, la reconfirmación de la
/// contraseña y la comprobación del segundo factor. Un solo texto y un solo identificador, sin el email.
/// </summary>
internal static partial class LockoutLog
{
    [LoggerMessage(EventId = SecurityEventIds.AccountLockedOut, Level = LogLevel.Warning,
        Message = "Cuenta bloqueada tras {FailedAttempts} intentos fallidos durante {LockoutMinutes} minutos. UserId {UserId}")]
    public static partial void AccountLockedOut(ILogger logger, Guid userId, int failedAttempts, int lockoutMinutes);

    [LoggerMessage(EventId = SecurityEventIds.LoginRejectedWhileLocked, Level = LogLevel.Warning,
        Message = "Intento de login con la cuenta bloqueada. UserId {UserId}")]
    public static partial void LoginRejectedWhileLocked(ILogger logger, Guid userId);
}

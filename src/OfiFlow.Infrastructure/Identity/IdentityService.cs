using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

public sealed partial class IdentityService(
    ApplicationDbContext dbContext,
    ILogger<IdentityService> logger,
    TimeProvider? timeProvider = null,
    IPasswordChecker? passwordChecker = null) : IIdentityService
{
    private static readonly PasswordHasher<ApplicationUser> PasswordHasher = new();

    // Los dos últimos parámetros son opcionales a propósito: en producción los inyecta el contenedor (la hora y el
    // verificador reales); los tests pueden pasar un reloj controlado o un verificador que cuente las llamadas.
    private readonly IPasswordChecker passwordChecker = passwordChecker ?? PasswordHasherChecker.Instance;
    private readonly AccountLockout lockout = new(dbContext, timeProvider ?? TimeProvider.System);

    public async Task<IdentityCreationResult> CreateUserAsync(Guid id, string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var alreadyExists = await dbContext.ApplicationUsers
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (alreadyExists)
        {
            return IdentityCreationResult.Failure([IdentityErrors.EmailAlreadyRegistered]);
        }

        var user = new ApplicationUser(id, email);
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        // Deliberadamente sin SaveChangesAsync: RegisterCommandHandler confirma esta fila
        // junto con Tenant/User/TenantUser en una única transacción (spec 002).
        dbContext.ApplicationUsers.Add(user);

        return IdentityCreationResult.Success();
    }

    public Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        return dbContext.ApplicationUsers.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await dbContext.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return;
        }

        // Deliberadamente sin SaveChangesAsync, igual que CreateUserAsync: el Handler confirma esta
        // baja junto con la pertenencia, el User y los tokens en una única transacción (spec 009 R14).
        dbContext.ApplicationUsers.Remove(user);

        // El segundo factor (spec 010 R20): ninguna de estas tablas tiene clave foránea hacia la cuenta (como
        // RefreshToken), así que no se borrarían solas y quedarían filas huérfanas con secretos de alguien que ya no existe.
        await MfaStore.RemoveAllAsync(dbContext, id, cancellationToken);
    }


    public async Task<Guid?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var user = await dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new { u.Id, u.PasswordHash })
            .FirstOrDefaultAsync(cancellationToken);

        if (user?.PasswordHash is null)
        {
            // Sin cuenta: se hace igualmente una verificación completa (contra un hash ficticio), para que la
            // respuesta no tarde menos que cuando el email existe (spec 010 R19, NEXT de ADR-009).
            // Sin el email en el log: es un dato personal, y en un ataque de credential stuffing el log
            // se llenaría de emails de terceros (ADR-009 R5). La IP la añade el scope de la API.
            passwordChecker.Verify(null, password);
            LogLoginFailed(logger, "UnknownEmail", null);
            return null;
        }

        // El intento se RESERVA antes de comprobar la contraseña (R18): es lo que impide que peticiones
        // simultáneas se cuelen mientras el contador aún no ha subido.
        var reservation = await lockout.TryReserveAsync(user.Id, cancellationToken);

        if (!reservation.Reserved)
        {
            // Bloqueada: ni la contraseña correcta entra (R16). Pero se hace la misma verificación completa,
            // contra el hash ficticio, para que ni siquiera el tiempo delate que está bloqueada (R19).
            passwordChecker.Verify(null, password);
            LockoutLog.LoginRejectedWhileLocked(logger, user.Id);
            return null;
        }

        if (!passwordChecker.Verify(user.PasswordHash, password))
        {
            LogLoginFailed(logger, "WrongPassword", user.Id);

            if (reservation.LockApplied)
            {
                LockoutLog.AccountLockedOut(logger, user.Id, reservation.FailedCount, (int)AuthenticationPolicy.LockoutDuration.TotalMinutes);
            }

            return null;
        }

        // Contraseña correcta y, por ahora, acceso completo: el contador vuelve a cero. Cuando exista el segundo
        // factor (bloque 5), una cuenta que lo tenga NO llega aquí: devolverá la reserva (ReleaseAsync) en vez de
        // ponerlo a cero, para que quien conoce la contraseña no pueda reiniciar el contador (R17).
        await lockout.ClearAsync(user.Id, cancellationToken);

        return user.Id;
    }

    public async Task<bool> ConfirmPasswordAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        var hash = await dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PasswordHash)
            .FirstOrDefaultAsync(cancellationToken);

        if (hash is null)
        {
            passwordChecker.Verify(null, password);
            return false;
        }

        // Igual que el login: el intento se reserva antes de comprobar (R18), y bloqueada o errónea no se distinguen.
        var reservation = await lockout.TryReserveAsync(userId, cancellationToken);

        if (!reservation.Reserved)
        {
            passwordChecker.Verify(null, password);
            LockoutLog.LoginRejectedWhileLocked(logger, userId);
            return false;
        }

        if (!passwordChecker.Verify(hash, password))
        {
            LogLoginFailed(logger, "ReauthenticationFailed", userId);

            if (reservation.LockApplied)
            {
                LockoutLog.AccountLockedOut(logger, userId, reservation.FailedCount, (int)AuthenticationPolicy.LockoutDuration.TotalMinutes);
            }

            return false;
        }

        // No es un inicio de sesión: la persona ya tenía sesión. Devuelve la reserva sin poner el contador a cero
        // (si no, un token robado más la contraseña reiniciaría el contador de otro ataque en curso).
        await lockout.ReleaseAsync(userId, cancellationToken);

        return true;
    }

    /// <summary>
    /// Solo comprueba la contraseña: <b>no cuenta intentos, no bloquea y no registra nada</b>. No está en
    /// <see cref="IIdentityService"/> a propósito: para entrar siempre se usa <see cref="AuthenticateAsync"/>, que sí.
    /// Existe para el resto de Infrastructure y para los tests que solo necesitan saber si unas credenciales existen.
    /// Un test de arquitectura impide usarlo fuera de este fichero.
    /// </summary>
    public async Task<Guid?> ValidateCredentialsWithoutLockoutAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var user = await dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new { u.Id, u.PasswordHash })
            .FirstOrDefaultAsync(cancellationToken);

        return user?.PasswordHash is not null && passwordChecker.Verify(user.PasswordHash, password) ? user.Id : null;
    }

    [LoggerMessage(EventId = SecurityEventIds.LoginFailed, Level = LogLevel.Warning,
        Message = "Login fallido. Motivo {Reason}, UserId {UserId}")]
    private static partial void LogLoginFailed(ILogger logger, string reason, Guid? userId);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El almacén y las comprobaciones del segundo factor (ADR-014, spec 010). Lo delicado, en un solo sitio:
/// <list type="bullet">
/// <item>El secreto se cifra al guardarlo y solo se descifra aquí, en memoria, para calcular el código.</item>
/// <item>Un código TOTP no se acepta dos veces: el último paso aceptado se anota con un <c>UPDATE</c> condicional
/// (<c>... WHERE LastUsedTimeStep &lt; @paso</c>), que es atómico: de dos peticiones simultáneas con el mismo código,
/// solo una cambia una fila.</item>
/// <item>Un código de recuperación se consume igual: <c>UPDATE ... WHERE UsedAt IS NULL</c>, una fila o ninguna.</item>
/// <item>Comprobar el segundo factor de una cuenta ya activa <b>reserva un intento</b> del bloqueo de cuenta, igual que
/// la contraseña: un fallo suma, un acierto lo devuelve.</item>
/// </list>
/// </summary>
public sealed partial class MfaService(
    ApplicationDbContext dbContext,
    ISecretProtector protector,
    ILogger<MfaService> logger,
    TimeProvider? timeProvider = null) : IMfaService
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly AccountLockout lockout = new(dbContext, timeProvider ?? TimeProvider.System);

    public async Task<MfaStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var confirmed = await dbContext.MfaCredentials
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (bool?)(c.ConfirmedAt != null))
            .FirstOrDefaultAsync(cancellationToken);

        if (confirmed is null)
        {
            return new MfaStatus(IsEnabled: false, SetupPending: false, RecoveryCodesRemaining: 0);
        }

        if (confirmed == false)
        {
            return new MfaStatus(IsEnabled: false, SetupPending: true, RecoveryCodesRemaining: 0);
        }

        return new MfaStatus(IsEnabled: true, SetupPending: false, await CountRecoveryCodesAsync(userId, cancellationToken));
    }

    public async Task<string> BeginSetupAsync(Guid userId, byte[] secret, CancellationToken cancellationToken)
    {
        var credential = await dbContext.MfaCredentials.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

        if (credential is { IsConfirmed: true })
        {
            // El Handler ya lo comprueba; esto es la última barrera: nunca se pisa el secreto de un MFA activo.
            throw new InvalidOperationException("El segundo factor ya está activo; hay que desactivarlo antes de pedir otro secreto.");
        }

        var cipher = protector.Protect(secret, userId.ToByteArray());

        if (credential is null)
        {
            dbContext.MfaCredentials.Add(MfaCredential.CreatePending(userId, cipher));
        }
        else
        {
            credential.ReplaceSecret(cipher);
        }

        return await dbContext.ApplicationUsers
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("La cuenta no existe.");
    }

    public async Task<SetupOutcome> ConfirmSetupAsync(
        Guid userId, string code, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken)
    {
        var pending = await dbContext.MfaCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

        if (pending is null || pending.IsConfirmed)
        {
            return SetupOutcome.NoPendingSetup;
        }

        var secret = protector.Unprotect(pending.SecretCipher, userId.ToByteArray());
        var check = Totp.Verify(secret, code, time.GetUtcNow(), pending.LastUsedTimeStep);

        if (check.Outcome != TotpOutcome.Valid)
        {
            LogRejected(logger, userId, check.Outcome == TotpOutcome.Replayed ? Reasons.CodeAlreadyUsed : Reasons.WrongCode);
            return SetupOutcome.InvalidCode;
        }

        if (!await TryRecordStepAsync(userId, check.Step, cancellationToken))
        {
            LogRejected(logger, userId, Reasons.CodeAlreadyUsed);
            return SetupOutcome.InvalidCode;
        }

        // Se relee la fila DESPUÉS de anotar el paso: la anotación cambió su versión, y guardar con la versión vieja
        // fallaría por concurrencia contra nuestra propia escritura.
        var credential = await dbContext.MfaCredentials.FirstAsync(c => c.UserId == userId, cancellationToken);
        credential.Confirm(time.GetUtcNow().UtcDateTime);

        foreach (var hash in recoveryCodeHashes)
        {
            dbContext.RecoveryCodes.Add(RecoveryCode.Create(userId, hash));
        }

        return SetupOutcome.Confirmed;
    }

    public Task<SecondFactorResult> VerifySecondFactorAsync(Guid userId, string code, CancellationToken cancellationToken) =>
        VerifyAsync(userId, code, completesLogin: false, cancellationToken);

    /// <param name="completesLogin">
    /// <c>true</c> en el segundo paso del login (acceso completo: el contador vuelve a cero, R17); <c>false</c> al comprobar el
    /// código en una operación con sesión (devuelve la reserva sin reiniciar nada).
    /// </param>
    private async Task<SecondFactorResult> VerifyAsync(Guid userId, string code, bool completesLogin, CancellationToken cancellationToken)
    {
        // El intento se reserva ANTES de comprobar el código (spec 010 R18), igual que con la contraseña: sin esto,
        // peticiones simultáneas probarían muchos códigos antes de que el contador subiera.
        var reservation = await lockout.TryReserveAsync(userId, cancellationToken);

        if (!reservation.Reserved)
        {
            LogRejected(logger, userId, Reasons.AccountLocked);
            LockoutLog.LoginRejectedWhileLocked(logger, userId);
            return SecondFactorResult.Rejected;
        }

        var (result, reason) = await CheckAsync(userId, code, cancellationToken);

        if (result.Valid)
        {
            if (completesLogin)
            {
                await lockout.ClearAsync(userId, cancellationToken);
            }
            else
            {
                // No es un inicio de sesión: devuelve la reserva sin poner el contador a cero.
                await lockout.ReleaseAsync(userId, cancellationToken);
            }

            return result;
        }

        LogRejected(logger, userId, reason);

        if (reservation.LockApplied)
        {
            LockoutLog.AccountLockedOut(logger, userId, reservation.FailedCount, (int)AuthenticationPolicy.LockoutDuration.TotalMinutes);
        }

        return SecondFactorResult.Rejected;
    }

    public async Task CreateChallengeAsync(Guid userId, string tokenHash, DateTime expiresAt, ChallengeAudience audience, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        // Los desafíos que ya no sirven (caducados, canjeados o agotados) se borran al crear uno nuevo: la tabla solo crece
        // con los desafíos vivos de la cuenta, no con cada contraseña correcta que se ponga.
        dbContext.MfaChallenges.RemoveRange(await dbContext.MfaChallenges
            .Where(c => c.UserId == userId
                        && (c.ExpiresAt <= now || c.ConsumedAt != null || c.FailedAttempts >= AuthenticationPolicy.ChallengeMaxFailedAttempts))
            .ToListAsync(cancellationToken));

        dbContext.MfaChallenges.Add(MfaChallenge.Create(userId, tokenHash, expiresAt, audience));
    }

    public async Task<ChallengeRedemption> RedeemChallengeAsync(string tokenHash, string code, ChallengeAudience audience, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var challenge = await dbContext.MfaChallenges.AsNoTracking().FirstOrDefaultAsync(c => c.TokenHash == tokenHash, cancellationToken);

        // Un desafío que no sirve (desconocido, usado, caducado o agotado) no cuenta como intento contra la cuenta: no hay
        // un intento de adivinar nada, y contarlo dejaría a cualquiera bloquear una cuenta ajena con desafíos inventados.
        var invalidReason = challenge switch
        {
            null => Reasons.ChallengeUnknown,
            // De otro mundo (spec 011 R9): se trata como uno desconocido, ANTES de comprobar el código, para no gastar ni un
            // intento de la cuenta ni un código de recuperación con un desafío que no puede canjearse aquí.
            _ when challenge.Audience != audience => Reasons.ChallengeWrongAudience,
            { ConsumedAt: not null } => Reasons.ChallengeUsed,
            _ when challenge.ExpiresAt <= now => Reasons.ChallengeExpired,
            _ when challenge.FailedAttempts >= AuthenticationPolicy.ChallengeMaxFailedAttempts => Reasons.ChallengeExhausted,
            _ => null
        };

        if (invalidReason is not null)
        {
            LogRejected(logger, challenge?.UserId, invalidReason);
            return ChallengeRedemption.Rejected;
        }

        // El código es lo que se comprueba de verdad: reserva un intento de la cuenta, y su acierto la deja a cero (R17).
        var result = await VerifyAsync(challenge!.UserId, code, completesLogin: true, cancellationToken);

        if (!result.Valid)
        {
            // Cada código erróneo gasta uno de los 5 intentos del desafío; al llegar al tope, el desafío muere (R11).
            await dbContext.MfaChallenges
                .Where(c => c.Id == challenge.Id && c.ConsumedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1), cancellationToken);

            return ChallengeRedemption.Rejected;
        }

        // Canjear es UNA sentencia condicional: de dos canjes simultáneos del mismo desafío, solo uno cambia la fila.
        var consumed = await dbContext.MfaChallenges
            .Where(c => c.Id == challenge.Id
                        && c.ConsumedAt == null
                        && c.ExpiresAt > now
                        && c.FailedAttempts < AuthenticationPolicy.ChallengeMaxFailedAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ConsumedAt, now), cancellationToken);

        if (consumed != 1)
        {
            LogRejected(logger, challenge.UserId, Reasons.ChallengeLostRace);
            return ChallengeRedemption.Rejected;
        }

        return new ChallengeRedemption(Succeeded: true, challenge.UserId);
    }

    public async Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken)
    {
        dbContext.RecoveryCodes.RemoveRange(await dbContext.RecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken));

        foreach (var hash in recoveryCodeHashes)
        {
            dbContext.RecoveryCodes.Add(RecoveryCode.Create(userId, hash));
        }
    }

    public Task RemoveAllAsync(Guid userId, CancellationToken cancellationToken) =>
        MfaStore.RemoveAllAsync(dbContext, userId, cancellationToken);

    private async Task<(SecondFactorResult Result, string Reason)> CheckAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var credential = await dbContext.MfaCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

        if (credential is null || !credential.IsConfirmed)
        {
            return (SecondFactorResult.Rejected, Reasons.NoSecondFactor);
        }

        if (Totp.IsWellFormed(code))
        {
            var secret = protector.Unprotect(credential.SecretCipher, userId.ToByteArray());
            var check = Totp.Verify(secret, code, time.GetUtcNow(), credential.LastUsedTimeStep);

            return check.Outcome switch
            {
                TotpOutcome.Valid when await TryRecordStepAsync(userId, check.Step, cancellationToken) =>
                    (new SecondFactorResult(Valid: true, UsedRecoveryCode: false, await CountRecoveryCodesAsync(userId, cancellationToken)), string.Empty),
                TotpOutcome.Valid or TotpOutcome.Replayed => (SecondFactorResult.Rejected, Reasons.CodeAlreadyUsed),
                _ => (SecondFactorResult.Rejected, Reasons.WrongCode)
            };
        }

        var normalized = RecoveryCodes.Normalize(code);

        if (normalized is null)
        {
            return (SecondFactorResult.Rejected, Reasons.MalformedCode);
        }

        var hash = RecoveryCodes.Hash(normalized);
        var now = time.GetUtcNow().UtcDateTime;

        // Consumir el código es una sola sentencia condicional: de dos usos simultáneos, uno cambia la fila y el otro no.
        var consumed = await dbContext.RecoveryCodes
            .Where(c => c.UserId == userId && c.CodeHash == hash && c.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.UsedAt, now), cancellationToken);

        if (consumed != 1)
        {
            return (SecondFactorResult.Rejected, Reasons.WrongRecoveryCode);
        }

        var remaining = await CountRecoveryCodesAsync(userId, cancellationToken);
        LogRecoveryCodeUsed(logger, userId, remaining);

        return (new SecondFactorResult(Valid: true, UsedRecoveryCode: true, remaining), string.Empty);
    }

    /// <summary>Anota el último paso aceptado solo si es posterior al guardado. Una fila cambiada = este código ganó.</summary>
    private async Task<bool> TryRecordStepAsync(Guid userId, long step, CancellationToken cancellationToken) =>
        await dbContext.MfaCredentials
            .Where(c => c.UserId == userId && c.LastUsedTimeStep < step)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.LastUsedTimeStep, step), cancellationToken) == 1;

    private Task<int> CountRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.RecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken);

    /// <summary>Los motivos que se registran (no se devuelven al cliente: quien lo intenta no aprende cuál fue).</summary>
    private static class Reasons
    {
        public const string WrongCode = "WrongCode";
        public const string CodeAlreadyUsed = "CodeAlreadyUsed";
        public const string WrongRecoveryCode = "WrongRecoveryCode";
        public const string MalformedCode = "MalformedCode";
        public const string AccountLocked = "AccountLocked";
        public const string NoSecondFactor = "NoSecondFactor";
        public const string ChallengeUnknown = "ChallengeUnknown";
        public const string ChallengeWrongAudience = "ChallengeWrongAudience";
        public const string ChallengeUsed = "ChallengeUsed";
        public const string ChallengeExpired = "ChallengeExpired";
        public const string ChallengeExhausted = "ChallengeExhausted";
        public const string ChallengeLostRace = "ChallengeLostRace";
    }

    [LoggerMessage(EventId = SecurityEventIds.MfaVerificationFailed, Level = LogLevel.Warning,
        Message = "Código del segundo factor rechazado. Motivo {Reason}, UserId {UserId}")]
    private static partial void LogRejected(ILogger logger, Guid? userId, string reason);

    [LoggerMessage(EventId = SecurityEventIds.RecoveryCodeUsed, Level = LogLevel.Warning,
        Message = "Código de recuperación usado; quedan {Remaining}. UserId {UserId}")]
    private static partial void LogRecoveryCodeUsed(ILogger logger, Guid userId, int remaining);
}

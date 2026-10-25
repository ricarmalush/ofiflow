namespace OfiFlow.Application.Common.Abstractions;

/// <summary>Cómo está el segundo factor de una cuenta.</summary>
/// <param name="IsEnabled">Activo: el secreto está confirmado y el login pide un código.</param>
/// <param name="SetupPending">Se pidió un secreto pero aún no se confirmó con un primer código (no cuenta como activo).</param>
/// <param name="RecoveryCodesRemaining">Códigos de recuperación sin usar (0 si el MFA no está activo).</param>
public sealed record MfaStatus(bool IsEnabled, bool SetupPending, int RecoveryCodesRemaining);

public enum SetupOutcome
{
    /// <summary>El código era válido: el secreto queda confirmado y los códigos de recuperación guardados.</summary>
    Confirmed,

    /// <summary>No hay un secreto pendiente de confirmar (no se pidió, o ya está confirmado).</summary>
    NoPendingSetup,

    /// <summary>El código no es válido (erróneo, de otro paso o ya usado).</summary>
    InvalidCode
}

/// <param name="Valid">El segundo factor se ha demostrado.</param>
/// <param name="UsedRecoveryCode">Se demostró con un código de recuperación (que queda consumido).</param>
/// <param name="RecoveryCodesRemaining">Cuántos códigos de recuperación quedan tras esta comprobación.</param>
public sealed record SecondFactorResult(bool Valid, bool UsedRecoveryCode, int RecoveryCodesRemaining)
{
    public static SecondFactorResult Rejected { get; } = new(false, false, 0);
}

/// <summary>
/// El almacén y las comprobaciones del segundo factor (ADR-014, spec 010), implementado en Infrastructure: cifra el
/// secreto, verifica los códigos de forma atómica y cuenta los fallos para el bloqueo de cuenta. Application no ve
/// secretos descifrados ni entidades de credenciales. Los métodos que cambian el estado no confirman por su cuenta
/// (lo hace el Handler en su único SaveChanges, junto con el cierre de sesiones), salvo las anotaciones atómicas de
/// "este código ya se usó" y de los intentos fallidos, que se escriben en el acto a propósito.
/// </summary>
public interface IMfaService
{
    Task<MfaStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Guarda el secreto como <b>pendiente</b> (cifrado), sustituyendo otro pendiente si lo había. Exige que el MFA no
    /// esté ya activo. Devuelve el email de la cuenta, que la app del móvil usa como nombre de la cuenta.
    /// </summary>
    Task<string> BeginSetupAsync(Guid userId, byte[] secret, CancellationToken cancellationToken);

    /// <summary>
    /// Comprueba el código contra el secreto pendiente. Si vale, lo confirma y guarda los hashes de los códigos de
    /// recuperación. Los fallos <b>no</b> cuentan para el bloqueo de cuenta (se limitan por IP).
    /// </summary>
    Task<SetupOutcome> ConfirmSetupAsync(Guid userId, string code, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken);

    /// <summary>
    /// Comprueba un código TOTP o de recuperación de una cuenta con el MFA activo. Cada comprobación <b>reserva un intento</b>
    /// del bloqueo de cuenta: un fallo cuenta, un acierto lo devuelve. Un código TOTP no se acepta dos veces y uno de
    /// recuperación se consume, ambos de forma atómica.
    /// </summary>
    Task<SecondFactorResult> VerifySecondFactorAsync(Guid userId, string code, CancellationToken cancellationToken);

    /// <summary>Sustituye todos los códigos de recuperación por otros nuevos (los anteriores dejan de valer).</summary>
    Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken);

    /// <summary>
    /// Crea un desafío del login en dos pasos (solo su hash) y borra los de la cuenta que ya no sirven (caducados,
    /// canjeados o agotados), para que la tabla no crezca con cada contraseña correcta. No confirma: lo hace el Handler.
    /// El desafío nace para un <paramref name="audience"/> y solo se canjea en él (spec 011 R9).
    /// </summary>
    Task CreateChallengeAsync(Guid userId, string tokenHash, DateTime expiresAt, ChallengeAudience audience, CancellationToken cancellationToken);

    /// <summary>
    /// Canjea un desafío con un código (TOTP o de recuperación): es el segundo paso del login. Solo tiene éxito si el
    /// desafío existe, no caducó, no se usó y no agotó sus intentos, y si el código es válido. <b>Todos los fallos son
    /// iguales para quien llama</b> (el motivo real va al registro). Cada código erróneo suma un intento al desafío y al
    /// bloqueo de la cuenta; un acierto pone el contador de la cuenta a cero (es un acceso completo, R17). Escribe en el
    /// acto (no necesita SaveChanges). Un desafío de OTRO <paramref name="audience"/> se trata como uno desconocido: ni se
    /// comprueba el código ni se gasta ningún intento (spec 011 R9).
    /// </summary>
    Task<ChallengeRedemption> RedeemChallengeAsync(string tokenHash, string code, ChallengeAudience audience, CancellationToken cancellationToken);

    /// <summary>Borra el secreto, los códigos de recuperación y los desafíos de la cuenta. Idempotente.</summary>
    Task RemoveAllAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// En qué mundo vale un desafío del segundo paso (spec 011 R9): el de las empresas o el de la plataforma. Un desafío no
/// cruza de uno a otro: el de empresa solo se canjea por una sesión de empresa, y el de plataforma, por una de plataforma.
/// </summary>
public enum ChallengeAudience
{
    Tenant,
    Platform
}

/// <summary>El resultado de canjear un desafío.</summary>
/// <param name="Succeeded">El desafío era bueno y el código válido: el segundo factor se ha superado.</param>
/// <param name="UserId">La cuenta del desafío; solo significa algo si <paramref name="Succeeded"/> es verdadero.</param>
public sealed record ChallengeRedemption(bool Succeeded, Guid UserId)
{
    public static ChallengeRedemption Rejected { get; } = new(false, Guid.Empty);
}

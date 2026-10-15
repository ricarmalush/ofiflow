namespace OfiFlow.Application.Identity;

/// <summary>
/// Los números de la política de autenticación (ADR-014, spec 010), en un único sitio para que cambiarlos sea una
/// decisión visible y no un valor suelto en un handler. Son valores iniciales que se revisan con datos reales. Un
/// test los fija: cambiar uno obliga a tocar el test y, con él, la spec.
/// </summary>
public static class AuthenticationPolicy
{
    /// <summary>Fallos de contraseña o de segundo factor que bloquean la cuenta (R16).</summary>
    public const int LockoutThreshold = 5;

    /// <summary>Cuánto dura el bloqueo (R16).</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Cuánto vive el desafío del login en dos pasos (R11).</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Códigos erróneos que admite un desafío antes de morir (R11).</summary>
    public const int ChallengeMaxFailedAttempts = 5;

    /// <summary>Cuántos códigos de recuperación se generan (R9).</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>El nombre con el que la app del móvil etiqueta la cuenta.</summary>
    public const string TotpIssuer = "OfiFlow";
}

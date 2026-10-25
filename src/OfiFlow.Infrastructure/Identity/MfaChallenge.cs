using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El desafío del login en dos pasos (ADR-014, spec 010 R11): lo que recibe quien ya puso bien la contraseña y se
/// canjea por una sesión con un código. Es un valor aleatorio y opaco del que solo se guarda el hash (no es un JWT,
/// así que no se puede presentar como token de acceso). Como las demás credenciales, no es de una empresa.
/// </summary>
public sealed class MfaChallenge
{
    public const int TokenHashMaxLength = 64;

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>En qué mundo vale (spec 011 R9): un desafío de empresa solo se canjea por una sesión de empresa, y uno de plataforma, por una de plataforma.</summary>
    public ChallengeAudience Audience { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Códigos erróneos recibidos con este desafío; al llegar al tope muere (R11).</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Cuándo se canjeó. Nulo mientras vale; se usa una sola vez.</summary>
    public DateTime? ConsumedAt { get; private set; }

    private MfaChallenge()
    {
        // Requerido por EF Core.
    }

    public static MfaChallenge Create(Guid userId, string tokenHash, DateTime expiresAt, ChallengeAudience audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenHash.Length, TokenHashMaxLength);

        return new MfaChallenge { Id = Guid.NewGuid(), UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt, Audience = audience };
    }
}

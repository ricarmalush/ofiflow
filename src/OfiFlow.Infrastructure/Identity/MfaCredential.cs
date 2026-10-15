namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El secreto TOTP de una cuenta (ADR-014, spec 010). Como <see cref="ApplicationUser"/> y <see cref="RefreshToken"/>,
/// es una credencial de la cuenta y no de una empresa: no es <c>ITenantOwned</c> (ADR-007). Una cuenta tiene como
/// mucho una, de ahí que su clave sea el propio <see cref="UserId"/>.
/// </summary>
public sealed class MfaCredential
{
    /// <summary>Cabe de sobra el formato de <see cref="AesGcmSecretProtector"/> para un secreto de 20 bytes (68 caracteres).</summary>
    public const int SecretCipherMaxLength = 200;

    public Guid UserId { get; private set; }

    /// <summary>El secreto, cifrado. Nunca se guarda ni se registra en claro.</summary>
    public string SecretCipher { get; private set; } = string.Empty;

    /// <summary>Cuándo se confirmó con un primer código. Nulo mientras sigue <b>pendiente</b>: aún no cuenta como MFA activo.</summary>
    public DateTime? ConfirmedAt { get; private set; }

    /// <summary>El último paso de 30 s aceptado: un código de ese paso o de uno anterior no vuelve a valer (R4).</summary>
    public long LastUsedTimeStep { get; private set; }

    public bool IsConfirmed => ConfirmedAt is not null;

    private MfaCredential()
    {
        // Requerido por EF Core.
    }

    /// <summary>Un secreto recién pedido, sin confirmar todavía.</summary>
    public static MfaCredential CreatePending(Guid userId, string secretCipher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretCipher);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(secretCipher.Length, SecretCipherMaxLength);

        return new MfaCredential { UserId = userId, SecretCipher = secretCipher };
    }
}

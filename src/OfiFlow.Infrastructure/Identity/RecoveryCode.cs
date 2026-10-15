namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Un código de recuperación de una cuenta (ADR-014, spec 010 R9). Solo se guarda su hash: el código en claro se
/// muestra una única vez al generarlo. Como las demás credenciales, no es de una empresa.
/// </summary>
public sealed class RecoveryCode
{
    public const int CodeHashMaxLength = 64;

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string CodeHash { get; private set; } = string.Empty;

    /// <summary>Cuándo se usó. Nulo mientras vale; un código usado no vuelve a servir.</summary>
    public DateTime? UsedAt { get; private set; }

    private RecoveryCode()
    {
        // Requerido por EF Core.
    }

    public static RecoveryCode Create(Guid userId, string codeHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(codeHash.Length, CodeHashMaxLength);

        return new RecoveryCode { Id = Guid.NewGuid(), UserId = userId, CodeHash = codeHash };
    }
}

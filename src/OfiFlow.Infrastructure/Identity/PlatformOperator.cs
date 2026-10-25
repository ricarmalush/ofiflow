namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Un operador de la plataforma (spec 011, ADR-016): una cuenta normal más esta fila. <b>No es de una empresa</b> (ni
/// <c>ITenantOwned</c> ni filtro de tenant) y, por la regla R1, la cuenta no tiene ninguna pertenencia a una empresa. Es una
/// tabla aparte, y no un rol ni un booleano en la cuenta, para poder auditarla y protegerla por separado: un solo bit mal
/// cambiado en la cuenta daría acceso global.
/// </summary>
public sealed class PlatformOperator
{
    /// <summary>La cuenta que es operador; es la clave: una cuenta no puede ser operador dos veces.</summary>
    public Guid UserId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Cuándo se dio de baja. Nulo mientras sigue activo; la baja surte efecto en su siguiente petición (R10).</summary>
    public DateTime? DisabledAt { get; private set; }

    public bool IsActive => DisabledAt is null;

    private PlatformOperator()
    {
        // Requerido por EF Core.
    }

    public static PlatformOperator Create(Guid userId, DateTime utcNow) => new() { UserId = userId, CreatedAt = utcNow };

    /// <summary>Da de baja al operador. La fila no se borra: conserva quién fue y cuándo (la auditoría la referencia).</summary>
    public void Disable(DateTime utcNow)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("El operador ya está dado de baja.");
        }

        DisabledAt = utcNow;
    }
}

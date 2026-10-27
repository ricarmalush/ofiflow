namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Los operadores de la plataforma (spec 011, ADR-016): cuentas con acceso a los metadatos de todas las empresas. Un
/// operador es una cuenta normal más una fila aparte, y <b>nunca es miembro de ninguna empresa</b> (R1). Implementada en
/// Infrastructure, junto a las demás credenciales.
/// </summary>
public interface IPlatformOperators
{
    /// <summary>¿Es esta cuenta un operador <b>activo</b> (no dado de baja)? Es lo que se revalida en cada petición de plataforma (R10).</summary>
    Task<bool> IsActiveOperatorAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Registra la cuenta como operador. Falla, sin cambiar nada, si la cuenta <b>pertenece a una empresa</b> (R1: una cuenta
    /// es de empresa o de plataforma, nunca de las dos) o si ya es operador. Como las demás operaciones de credenciales,
    /// no confirma el cambio: lo hace quien llama en su único SaveChanges, junto con el resto del alta.
    /// </summary>
    Task<PlatformOperatorCreation> AddAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>
    /// Da de baja a un operador (rellena <c>DisabledAt</c>): surte efecto en su siguiente petición (R4, R10). Falso si la
    /// cuenta no es un operador o ya estaba dada de baja. No confirma el cambio.
    /// </summary>
    Task<bool> DisableAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken);
}

/// <summary>El resultado de registrar una cuenta como operador.</summary>
public enum PlatformOperatorCreation
{
    Added,

    /// <summary>La cuenta tiene una pertenencia a una empresa: no puede ser operador (R1).</summary>
    AccountBelongsToACompany,

    AlreadyAnOperator
}

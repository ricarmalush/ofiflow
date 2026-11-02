using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Identity;

/// <summary>
/// Un permiso de un solo uso para cambiar la contraseña de una cuenta que la olvidó (spec 018, ADR-021). Quien tiene el token
/// puede poner una contraseña nueva, así que el token es un secreto: <b>el valor en claro no se guarda nunca</b>, solo su hash (R3).
/// </summary>
/// <remarks>
/// Es de una <b>cuenta</b> y no de una empresa (R17): una persona puede pertenecer a varias y la recuperación ocurre sin empresa activa.
/// Por eso no es <c>ITenantOwned</c> y está declarada como entidad global. La fila no guarda el email, solo el Id de la cuenta.
/// La hora actual entra siempre por parámetro, para que las reglas de caducidad se puedan probar sin esperar.
/// </remarks>
public sealed class PasswordResetToken : Entity
{
    /// <summary>Cuánto dura un token (spec 018 R3, D4). Una hora, no los 7 días de una invitación: el daño de uno robado es mayor.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>Cuántos se pueden crear por cuenta y por hora (spec 018 R4): el límite que protege a la víctima de un buzón inundado.</summary>
    public const int MaxPerAccountPerHour = 3;

    /// <summary>Fuente única para EF Core: un SHA-256 en base64 son 44 caracteres.</summary>
    public const int TokenHashMaxLength = 64;

    /// <summary>La cuenta a la que pertenece (el mismo Id que <c>User</c> y las credenciales). No es un dato personal.</summary>
    public Guid UserId { get; private set; }

    /// <summary>SHA-256 del token, nunca el token (R3).</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Cuándo se consumió o se anuló; nulo mientras siga vigente. Las dos cosas lo dejan inservible y no se distinguen hacia fuera (R8).</summary>
    public DateTime? UsedAt { get; private set; }

    private PasswordResetToken()
    {
        // Requerido por EF Core.
    }

    private PasswordResetToken(Guid id, Guid userId, string tokenHash, DateTime createdAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = createdAt + Lifetime;
    }

    /// <param name="tokenHash">El hash del token, calculado fuera de Domain. Nunca el token en claro.</param>
    /// <param name="utcNow">La hora actual (UTC); la caducidad es esta hora más <see cref="Lifetime"/>.</param>
    public static PasswordResetToken Create(Guid userId, string tokenHash, DateTime utcNow)
    {
        // No es una regla de negocio sino un fallo de programación: una cuenta vacía o un hash vacío dejarían un token
        // imposible de buscar o de atribuir.
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenHash.Length, TokenHashMaxLength);

        return new PasswordResetToken(Guid.NewGuid(), userId, tokenHash, utcNow);
    }

    /// <summary>
    /// ¿Se puede usar ahora? Solo si no se consumió ni se anuló y no ha caducado. Caducar es una fecha, no un cambio de estado: el token
    /// <b>deja de valer</b> en el instante de <see cref="ExpiresAt"/> sin que nadie tenga que marcarlo.
    /// </summary>
    public bool IsUsableAt(DateTime utcNow) => UsedAt is null && utcNow < ExpiresAt;

    /// <summary>
    /// Lo consume: un restablecimiento correcto (R9b). Solo se puede si es utilizable; el motivo del rechazo no se distingue (R8).
    /// </summary>
    /// <exception cref="DomainException">Con <see cref="IdentityErrors.PasswordResetInvalid"/>, sea cual sea el motivo.</exception>
    public void Consume(DateTime utcNow)
    {
        if (!IsUsableAt(utcNow))
        {
            throw new DomainException(IdentityErrors.PasswordResetInvalid);
        }

        UsedAt = utcNow;
    }

    /// <summary>
    /// Lo anula: se pidió otro más reciente (R3, uno vigente por cuenta) o se restableció la contraseña por otra vía (R9b). No falla si ya
    /// estaba usado o caducado: anular lo que ya no vale no cambia nada y no debe romper al que lo pide.
    /// </summary>
    public void Invalidate(DateTime utcNow)
    {
        UsedAt ??= utcNow;
    }
}

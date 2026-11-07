using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Identity;

/// <summary>
/// Lo que escribió alguien que quiere crear una cuenta, <b>hasta que abre el enlace de su correo</b> (spec 021, ADR-023). No es una cuenta: no hay empresa, ni usuario, ni
/// credenciales, ni contraseña. La cuenta nace cuando se <b>canjea</b> (D1), y por eso quien registra el email de otra persona no consigue nada: sin acceso a su buzón, no hay nada que
/// canjear (y si la víctima abre el enlace, elige ella la contraseña, D5). Es un secreto de un solo uso: <b>el token no se guarda nunca</b>, solo su hash (R4).
/// </summary>
/// <remarks>
/// Es <b>global</b> (R4, spec 021): todavía no existe ninguna empresa, así que no es <c>ITenantOwned</c>. Guarda datos personales (nombre y email de alguien que aún no tiene cuenta) y por
/// eso caduca y se purga (R18). La hora actual entra siempre por parámetro, para que las reglas de caducidad se puedan probar sin esperar.
/// </remarks>
public sealed class PendingRegistration : Entity
{
    /// <summary>Cuánto dura el enlace (spec 021 R9, D3). Un día, no la hora de recuperar la contraseña: aquí no hay ninguna cuenta en juego hasta que se abre, y la persona puede tardar.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Cuántos se pueden crear por email y por hora (spec 021 R7): el límite que protege a la víctima de un buzón inundado desde muchas IP.</summary>
    public const int MaxPerEmailPerHour = 3;

    /// <summary>Fuente única para EF Core: un SHA-256 en base64 son 44 caracteres.</summary>
    public const int TokenHashMaxLength = 64;

    /// <summary>El email tal como se escribió: es la dirección a la que se envía el enlace y, al canjear, el email de contacto del usuario.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>El mismo email en mayúsculas (la regla del registro y del login, ADR-004): con él se busca y se garantiza <b>uno vigente por email</b> (R5).</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    public string CompanyName { get; private set; } = string.Empty;

    public string UserName { get; private set; } = string.Empty;

    /// <summary>La versión de las condiciones de uso que aceptó al pedir el registro (R16). Pasa al <c>User</c> al canjear.</summary>
    public string TermsVersion { get; private set; } = string.Empty;

    public DateTime TermsAcceptedAt { get; private set; }

    /// <summary>SHA-256 del token, nunca el token (R4).</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Cuándo se canjeó o se anuló; nulo mientras siga vigente. Las dos cosas lo dejan inservible y no se distinguen hacia fuera (R11).</summary>
    public DateTime? UsedAt { get; private set; }

    private PendingRegistration()
    {
        // Requerido por EF Core.
    }

    private PendingRegistration(Guid id, string email, string companyName, string userName, string termsVersion, string tokenHash, DateTime utcNow)
        : base(id)
    {
        Email = email;
        NormalizedEmail = email.ToUpperInvariant();
        CompanyName = companyName;
        UserName = userName;
        TermsVersion = termsVersion;
        TermsAcceptedAt = utcNow;
        TokenHash = tokenHash;
        CreatedAt = utcNow;
        ExpiresAt = utcNow + Lifetime;
    }

    /// <param name="email">Un email ya válido (lo comprobó el validador); aquí se vuelve a comprobar y un valor inválido lanza.</param>
    /// <param name="tokenHash">El hash del token, calculado fuera de Domain. Nunca el token en claro.</param>
    /// <param name="utcNow">La hora actual (UTC); la caducidad es esta hora más <see cref="Lifetime"/>, y es también cuándo se aceptaron las condiciones.</param>
    public static PendingRegistration Create(string email, string companyName, string userName, string termsVersion, string tokenHash, DateTime utcNow)
    {
        // El email, con la regla común: un valor inválido da el mismo error de siempre (y el mensaje nunca lleva el valor).
        var validEmail = Common.Email.Create(email.Trim());

        // Lo demás no son reglas de negocio sino fallos de programación: el validador de Application ya las comprobó, y una fila sin nombre, con un nombre
        // que no cabe o sin hash sería imposible de canjear o de atribuir.
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(companyName.Trim().Length, Tenant.NameMaxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(userName.Trim().Length, User.NameMaxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(termsVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(termsVersion.Length, User.TermsVersionMaxLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenHash.Length, TokenHashMaxLength);

        return new PendingRegistration(Guid.NewGuid(), validEmail.Value, companyName.Trim(), userName.Trim(), termsVersion, tokenHash, utcNow);
    }

    /// <summary>
    /// ¿Se puede canjear ahora? Solo si no se canjeó ni se anuló y no ha caducado. Caducar es una fecha, no un cambio de estado: deja de valer en el instante de
    /// <see cref="ExpiresAt"/> sin que nadie tenga que marcarlo.
    /// </summary>
    public bool IsUsableAt(DateTime utcNow) => UsedAt is null && utcNow < ExpiresAt;

    /// <summary>Lo canjea: se completó el registro (R10). Solo se puede si es utilizable; el motivo del rechazo no se distingue (R11).</summary>
    /// <exception cref="DomainException">Con <see cref="IdentityErrors.RegistrationInvalid"/>, sea cual sea el motivo.</exception>
    public void Consume(DateTime utcNow)
    {
        if (!IsUsableAt(utcNow))
        {
            throw new DomainException(IdentityErrors.RegistrationInvalid);
        }

        UsedAt = utcNow;
    }

    /// <summary>
    /// Lo anula: se pidió otro más reciente para el mismo email (R5, uno vigente por email). No falla si ya estaba usado o caducado: anular lo que ya no vale no cambia nada y no
    /// debe romper al que lo pide. Conserva la fecha del primer uso.
    /// </summary>
    public void Invalidate(DateTime utcNow)
    {
        UsedAt ??= utcNow;
    }
}

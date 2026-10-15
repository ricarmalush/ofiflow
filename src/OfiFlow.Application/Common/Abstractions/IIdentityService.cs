namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Abstracts credential management away from Application — implemented in Infrastructure
/// on top of ASP.NET Core Identity (ADR-004/007). Application never touches UserManager
/// or ApplicationUser directly.
/// </summary>
public interface IIdentityService
{
    Task<IdentityCreationResult> CreateUserAsync(Guid id, string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// ¿Ya hay una cuenta con este email? Se usa antes de invitar (spec 009 R6): no se invita a quien ya tiene
    /// cuenta, porque hoy una cuenta pertenece a una sola empresa. No revela nada que el registro no revele ya.
    /// </summary>
    Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Autentica con email y contraseña, <b>con bloqueo de cuenta</b> (ADR-014, spec 010). Cuenta cada intento,
    /// bloquea tras demasiados fallos y registra los eventos de seguridad. No hay otra forma de comprobar una
    /// contraseña desde Application: así no se puede olvidar el bloqueo.
    /// </summary>
    /// <returns>
    /// El Id de la cuenta si las credenciales son correctas; <c>null</c> en cualquier otro caso (email desconocido,
    /// contraseña errónea o cuenta bloqueada) sin revelar cuál: la respuesta y el tiempo son los mismos.
    /// </returns>
    Task<Guid?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Vuelve a pedir la contraseña a alguien que ya tiene sesión, antes de una operación sensible (activar o desactivar
    /// el segundo factor): un token de acceso robado no basta (spec 010 R5-R7). <b>Cuenta para el bloqueo de cuenta</b>:
    /// un fallo suma un intento, un acierto lo devuelve (no es un inicio de sesión, así que no pone el contador a cero).
    /// Falso si la contraseña es errónea o la cuenta está bloqueada, sin distinguirlo.
    /// </summary>
    Task<bool> ConfirmPasswordAsync(Guid userId, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Borra las credenciales de un usuario, y con ellas su email deja de estar ocupado (spec 009 R14d).
    /// Como <see cref="CreateUserAsync"/>, no confirma el cambio: lo hace el Handler en su único SaveChanges,
    /// junto con el resto del borrado, para que sea todo o nada. Si el usuario no existe, no hace nada.
    /// </summary>
    Task DeleteUserAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record IdentityCreationResult(bool Succeeded, IReadOnlyCollection<string> Errors)
{
    public static IdentityCreationResult Success() => new(true, []);

    public static IdentityCreationResult Failure(IEnumerable<string> errors) => new(false, errors.ToList());
}

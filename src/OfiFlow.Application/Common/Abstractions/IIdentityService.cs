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

    /// <returns>The user's Id if the credentials are valid, otherwise null — never reveals which of the two failed.</returns>
    Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken);

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

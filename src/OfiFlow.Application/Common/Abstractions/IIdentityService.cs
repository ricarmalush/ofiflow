namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Abstracts credential management away from Application — implemented in Infrastructure
/// on top of ASP.NET Core Identity (ADR-004/007). Application never touches UserManager
/// or ApplicationUser directly.
/// </summary>
public interface IIdentityService
{
    Task<IdentityCreationResult> CreateUserAsync(Guid id, string email, string password, CancellationToken cancellationToken);

    /// <returns>The user's Id if the credentials are valid, otherwise null — never reveals which of the two failed.</returns>
    Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken);
}

public sealed record IdentityCreationResult(bool Succeeded, IReadOnlyCollection<string> Errors)
{
    public static IdentityCreationResult Success() => new(true, []);

    public static IdentityCreationResult Failure(IEnumerable<string> errors) => new(false, errors.ToList());
}

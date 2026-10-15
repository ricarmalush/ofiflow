using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Tests.Common;

public sealed class FakeIdentityService : IIdentityService
{
    private readonly Dictionary<string, (Guid Id, string Password)> _users = [];

    public bool ShouldFailCreation { get; set; }

    public Task<IdentityCreationResult> CreateUserAsync(Guid id, string email, string password, CancellationToken cancellationToken)
    {
        if (ShouldFailCreation || _users.ContainsKey(email))
        {
            return Task.FromResult(IdentityCreationResult.Failure([IdentityErrors.EmailAlreadyRegistered]));
        }

        _users[email] = (id, password);
        return Task.FromResult(IdentityCreationResult.Success());
    }

    /// <summary>Ids de los usuarios cuyas credenciales se han borrado, para que los tests lo comprueben.</summary>
    public List<Guid> DeletedUsers { get; } = [];

    public Task DeleteUserAsync(Guid id, CancellationToken cancellationToken)
    {
        DeletedUsers.Add(id);
        var email = _users.FirstOrDefault(pair => pair.Value.Id == id).Key;
        if (email is not null)
        {
            _users.Remove(email);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(_users.Keys.Any(registered => string.Equals(registered, email, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Registra un email como ya ocupado, sin pasar por CreateUserAsync.</summary>
    public void AddExistingAccount(string email) => _users[email] = (Guid.NewGuid(), "irrelevante");

    /// <summary>Las contraseñas confirmadas (reautenticación antes de una operación sensible), en orden, para comprobar a quién se le pidió.</summary>
    public List<(Guid UserId, string Password)> PasswordConfirmations { get; } = [];

    public Task<bool> ConfirmPasswordAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        PasswordConfirmations.Add((userId, password));

        return Task.FromResult(_users.Values.Any(user => user.Id == userId && user.Password == password));
    }

    /// <summary>Las cuentas que tienen el segundo factor activo: su contraseña sola no basta (spec 010 R10).</summary>
    public HashSet<Guid> AccountsWithSecondFactor { get; } = [];

    public Task<PasswordAuthentication?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (_users.TryGetValue(email, out var user) && user.Password == password)
        {
            return Task.FromResult<PasswordAuthentication?>(new PasswordAuthentication(user.Id, AccountsWithSecondFactor.Contains(user.Id)));
        }

        return Task.FromResult<PasswordAuthentication?>(null);
    }
}

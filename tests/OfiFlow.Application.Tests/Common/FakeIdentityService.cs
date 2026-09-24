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

    public Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (_users.TryGetValue(email, out var user) && user.Password == password)
        {
            return Task.FromResult<Guid?>(user.Id);
        }

        return Task.FromResult<Guid?>(null);
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

public sealed class IdentityService(ApplicationDbContext dbContext) : IIdentityService
{
    private static readonly PasswordHasher<ApplicationUser> PasswordHasher = new();

    public async Task<IdentityCreationResult> CreateUserAsync(Guid id, string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var alreadyExists = await dbContext.ApplicationUsers
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (alreadyExists)
        {
            return IdentityCreationResult.Failure(["El email ya está registrado."]);
        }

        var user = new ApplicationUser(id, email);
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        // Deliberadamente sin SaveChangesAsync: RegisterCommandHandler confirma esta fila
        // junto con Tenant/User/TenantUser en una única transacción (spec 002).
        dbContext.ApplicationUsers.Add(user);

        return IdentityCreationResult.Success();
    }

    public async Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var user = await dbContext.ApplicationUsers
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user?.PasswordHash is null)
        {
            return null;
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        return result == PasswordVerificationResult.Failed ? null : user.Id;
    }
}

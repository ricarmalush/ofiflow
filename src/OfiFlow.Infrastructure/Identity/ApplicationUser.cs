using Microsoft.AspNetCore.Identity;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Credentials only (ADR-007) — Domain.User holds the business data, sharing this same Id.
/// Deliberately treated as a plain EF entity, NOT registered through UserManager/
/// IdentityDbContext: IIdentityService.CreateUserAsync only Add()s it, so
/// RegisterCommandHandler can commit it in the same SaveChangesAsync as
/// Tenant/User/TenantUser (single-transaction requirement, spec 002).
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
    }

    public ApplicationUser(Guid id, string email)
    {
        Id = id;
        Email = email;
        UserName = email;
        NormalizedEmail = email.ToUpperInvariant();
        NormalizedUserName = email.ToUpperInvariant();
    }
}

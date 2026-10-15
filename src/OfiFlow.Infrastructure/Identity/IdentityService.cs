using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

public sealed partial class IdentityService(ApplicationDbContext dbContext, ILogger<IdentityService> logger) : IIdentityService
{
    private static readonly PasswordHasher<ApplicationUser> PasswordHasher = new();

    public async Task<IdentityCreationResult> CreateUserAsync(Guid id, string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var alreadyExists = await dbContext.ApplicationUsers
            .AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (alreadyExists)
        {
            return IdentityCreationResult.Failure([IdentityErrors.EmailAlreadyRegistered]);
        }

        var user = new ApplicationUser(id, email);
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        // Deliberadamente sin SaveChangesAsync: RegisterCommandHandler confirma esta fila
        // junto con Tenant/User/TenantUser en una única transacción (spec 002).
        dbContext.ApplicationUsers.Add(user);

        return IdentityCreationResult.Success();
    }

    public Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        return dbContext.ApplicationUsers.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await dbContext.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return;
        }

        // Deliberadamente sin SaveChangesAsync, igual que CreateUserAsync: el Handler confirma esta
        // baja junto con la pertenencia, el User y los tokens en una única transacción (spec 009 R14).
        dbContext.ApplicationUsers.Remove(user);

        // El segundo factor (spec 010 R20): ninguna de estas tablas tiene clave foránea hacia la cuenta (como
        // RefreshToken), así que no se borrarían solas y quedarían filas huérfanas con secretos de alguien que ya no existe.
        dbContext.MfaCredentials.RemoveRange(await dbContext.MfaCredentials.Where(c => c.UserId == id).ToListAsync(cancellationToken));
        dbContext.RecoveryCodes.RemoveRange(await dbContext.RecoveryCodes.Where(c => c.UserId == id).ToListAsync(cancellationToken));
        dbContext.MfaChallenges.RemoveRange(await dbContext.MfaChallenges.Where(c => c.UserId == id).ToListAsync(cancellationToken));
    }

    public async Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var user = await dbContext.ApplicationUsers
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user?.PasswordHash is null)
        {
            // Sin el email: es un dato personal, y en un ataque de credential stuffing el log
            // se llenaría de emails de terceros (ADR-009 R5). La IP la añade el scope de la API.
            LogLoginFailed(logger, "UnknownEmail", null);
            return null;
        }

        var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (result == PasswordVerificationResult.Failed)
        {
            LogLoginFailed(logger, "WrongPassword", user.Id);
            return null;
        }

        return user.Id;
    }

    [LoggerMessage(EventId = SecurityEventIds.LoginFailed, Level = LogLevel.Warning,
        Message = "Login fallido. Motivo {Reason}, UserId {UserId}")]
    private static partial void LogLoginFailed(ILogger logger, string reason, Guid? userId);
}

using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

public class IdentityServiceTests
{
    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options, new FixedTenantContext(Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateUserAsync_ThenValidateCredentials_Succeeds()
    {
        await using var db = CreateDbContext();
        var service = new IdentityService(db);

        var userId = Guid.NewGuid();
        var result = await service.CreateUserAsync(userId, "juan@example.com", "Password123!", CancellationToken.None);
        Assert.True(result.Succeeded);

        // CreateUserAsync deliberadamente no hace SaveChanges (ver comentario en IdentityService) —
        // aquí simulamos el SaveChanges único que en producción hace RegisterCommandHandler.
        await db.SaveChangesAsync(CancellationToken.None);

        var validatedUserId = await service.ValidateCredentialsAsync("juan@example.com", "Password123!", CancellationToken.None);
        Assert.Equal(userId, validatedUserId);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_WithWrongPassword_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var service = new IdentityService(db);

        await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await service.ValidateCredentialsAsync("juan@example.com", "incorrecta", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateUserAsync_WithAlreadyRegisteredEmail_Fails()
    {
        await using var db = CreateDbContext();
        var service = new IdentityService(db);

        await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "OtraPassword123!", CancellationToken.None);

        Assert.False(result.Succeeded);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Logging;
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
        var service = new IdentityService(db, NullLogger<IdentityService>.Instance);

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
        var service = new IdentityService(db, NullLogger<IdentityService>.Instance);

        await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await service.ValidateCredentialsAsync("juan@example.com", "incorrecta", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateUserAsync_WithAlreadyRegisteredEmail_Fails()
    {
        await using var db = CreateDbContext();
        var service = new IdentityService(db, NullLogger<IdentityService>.Instance);

        await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "OtraPassword123!", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("juan@example.com", "incorrecta", "WrongPassword")]
    [InlineData("desconocido@example.com", "Password123!", "UnknownEmail")]
    public async Task ValidateCredentialsAsync_WhenFails_LogsSecurityEventWithoutEmailOrPassword(
        string email, string password, string expectedReason)
    {
        await using var db = CreateDbContext();
        var logger = new ListLogger<IdentityService>();
        var service = new IdentityService(db, logger);

        await service.CreateUserAsync(Guid.NewGuid(), "juan@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        await service.ValidateCredentialsAsync(email, password, CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.LoginFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(expectedReason, entry.Message);
        Assert.DoesNotContain(email, entry.Message);
        Assert.DoesNotContain(password, entry.Message);
    }
}

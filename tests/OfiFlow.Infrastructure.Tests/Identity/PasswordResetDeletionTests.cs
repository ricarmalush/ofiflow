using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Dar de baja a una cuenta se lleva sus tokens de restablecimiento (spec 018 R17): la tabla no tiene clave foránea hacia la cuenta, así que no se borrarían
/// solos y quedarían filas huérfanas. Como el resto de la baja (spec 009 R14), no confirma el cambio por su cuenta: lo hace el Handler en su único SaveChanges.
/// </summary>
public class PasswordResetDeletionTests
{
    private const string Password = "Password123!";
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext Open(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options, new FixedTenantContext(Guid.NewGuid()));

    private static async Task<Guid> RegisterAsync(ApplicationDbContext db, string email)
    {
        var id = Guid.NewGuid();
        var result = await new IdentityService(db, NullLogger<IdentityService>.Instance).CreateUserAsync(id, email, Password, CancellationToken.None);
        Assert.True(result.Succeeded);
        await db.SaveChangesAsync(CancellationToken.None);
        return id;
    }

    private static async Task AddTokensAsync(ApplicationDbContext db, Guid userId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.PasswordResetTokens.Add(PasswordResetToken.Create(userId, $"hash-{userId:N}-{i}", Now));
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeleteUserAsync_RemovesTheTokensOfThatAccount_AndOnlyThoseOfThatAccount()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var gone = await RegisterAsync(db, "ana@example.com");
        var stays = await RegisterAsync(db, "luis@example.com");
        await AddTokensAsync(db, gone, 3);
        await AddTokensAsync(db, stays, 2);

        await new IdentityService(db, NullLogger<IdentityService>.Instance).DeleteUserAsync(gone, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        await using var check = Open(name);
        Assert.Equal(0, await check.PasswordResetTokens.CountAsync(t => t.UserId == gone));
        Assert.Equal(2, await check.PasswordResetTokens.CountAsync(t => t.UserId == stays));
    }

    [Fact]
    public async Task DeleteUserAsync_DoesNotCommitTheTokenRemovalByItself()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var userId = await RegisterAsync(db, "ana@example.com");
        await AddTokensAsync(db, userId, 2);

        await new IdentityService(db, NullLogger<IdentityService>.Instance).DeleteUserAsync(userId, CancellationToken.None);

        // Sin SaveChanges, lo confirmado sigue ahí: la baja es todo o nada y la confirma el Handler.
        await using var check = Open(name);
        Assert.Equal(2, await check.PasswordResetTokens.CountAsync(t => t.UserId == userId));
    }

    [Fact]
    public async Task DeleteUserAsync_AnAccountWithoutTokens_IsFine()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var userId = await RegisterAsync(db, "ana@example.com");

        await new IdentityService(db, NullLogger<IdentityService>.Instance).DeleteUserAsync(userId, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(0, await db.PasswordResetTokens.CountAsync());
    }

    [Fact]
    public async Task RemoveAllAsync_DoesNothingForAnAccountThatDoesNotExist()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var other = await RegisterAsync(db, "ana@example.com");
        await AddTokensAsync(db, other, 1);

        await PasswordResetService.RemoveAllAsync(db, Guid.NewGuid(), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(1, await db.PasswordResetTokens.CountAsync());
    }
}

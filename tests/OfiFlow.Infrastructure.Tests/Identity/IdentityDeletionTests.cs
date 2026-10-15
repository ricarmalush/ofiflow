using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Borrado de credenciales y de refresh tokens (spec 009 R14): lo que hace falta para quitar a un
/// miembro de la empresa sin dejar filas huérfanas ni la posibilidad de renovar la sesión.
/// Ninguno de los dos métodos confirma el cambio por su cuenta: lo hace el Handler en su único
/// SaveChanges, para que la baja sea todo o nada.
/// </summary>
public class IdentityDeletionTests
{
    private const string Password = "Password123!";

    private static readonly JwtOptions Jwt = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    /// <summary>Un contexto nuevo sobre la MISMA base en memoria, para ver lo que de verdad está confirmado.</summary>
    private static ApplicationDbContext Open(string databaseName) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options,
            new FixedTenantContext(Guid.NewGuid()));

    private static IdentityService Identity(ApplicationDbContext db) => new(db, NullLogger<IdentityService>.Instance);

    private static TokenService Tokens(ApplicationDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(Jwt), NullLogger<TokenService>.Instance);

    private static async Task<Guid> RegisterAsync(ApplicationDbContext db, string email)
    {
        var id = Guid.NewGuid();
        var result = await Identity(db).CreateUserAsync(id, email, Password, CancellationToken.None);
        Assert.True(result.Succeeded);
        await db.SaveChangesAsync(CancellationToken.None);
        return id;
    }

    // ===== Credenciales =====

    [Fact]
    public async Task DeleteUserAsync_RemovesTheCredentials_AndTheEmailIsFreeAgain()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var userId = await RegisterAsync(db, "ana@example.com");

        await Identity(db).DeleteUserAsync(userId, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Null(await Identity(db).ValidateCredentialsWithoutLockoutAsync("ana@example.com", Password, CancellationToken.None));

        // El email vuelve a poder usarse: se puede volver a invitar a esa persona (R14d).
        var again = await Identity(db).CreateUserAsync(Guid.NewGuid(), "ana@example.com", Password, CancellationToken.None);
        Assert.True(again.Succeeded);
    }

    [Fact]
    public async Task DeleteUserAsync_DoesNotCommitByItself()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var userId = await RegisterAsync(db, "ana@example.com");

        await Identity(db).DeleteUserAsync(userId, CancellationToken.None); // sin SaveChanges

        // Otro contexto, que solo ve lo confirmado, todavía la ve: el borrado no se ha aplicado.
        await using var other = Open(name);
        Assert.Equal(userId, await Identity(other).ValidateCredentialsWithoutLockoutAsync("ana@example.com", Password, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteUserAsync_ForAnUnknownId_DoesNothing()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var userId = await RegisterAsync(db, "ana@example.com");

        await Identity(db).DeleteUserAsync(Guid.NewGuid(), CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(userId, await Identity(db).ValidateCredentialsWithoutLockoutAsync("ana@example.com", Password, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteUserAsync_OnlyRemovesThatUser()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var ana = await RegisterAsync(db, "ana@example.com");
        var luis = await RegisterAsync(db, "luis@example.com");

        await Identity(db).DeleteUserAsync(ana, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Null(await Identity(db).ValidateCredentialsWithoutLockoutAsync("ana@example.com", Password, CancellationToken.None));
        Assert.Equal(luis, await Identity(db).ValidateCredentialsWithoutLockoutAsync("luis@example.com", Password, CancellationToken.None));
    }

    // ===== Refresh tokens =====

    private static async Task<(Guid UserId, Guid TenantId, string RefreshToken)> StartSessionAsync(ApplicationDbContext db)
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        db.TenantUsers.Add(TenantUser.Create(tenantId, userId, TenantRole.Technician));
        await db.SaveChangesAsync(CancellationToken.None);

        var issued = await Tokens(db).IssueTokensAsync(userId, tenantId, TenantRole.Technician, CancellationToken.None);
        return (userId, tenantId, issued.RefreshToken);
    }

    [Fact]
    public async Task DeleteRefreshTokensAsync_RemovesEveryTokenOfThatUser_RevokedOnesIncluded_AndNoOthers()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var ana = await StartSessionAsync(db);
        var luis = await StartSessionAsync(db);

        // Ana renueva una vez: queda un token revocado y uno activo.
        await Tokens(db).RotateRefreshTokenAsync(ana.RefreshToken, CancellationToken.None);
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.UserId == ana.UserId));

        await Tokens(db).DeleteRefreshTokensAsync(ana.UserId, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(0, await db.RefreshTokens.CountAsync(t => t.UserId == ana.UserId)); // sin huérfanas
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.UserId == luis.UserId)); // control: Luis intacto
    }

    [Fact]
    public async Task DeleteRefreshTokensAsync_ThenTheOldTokenCannotRenewTheSession_ButAnotherUsersCan()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var ana = await StartSessionAsync(db);
        var luis = await StartSessionAsync(db);

        await Tokens(db).DeleteRefreshTokensAsync(ana.UserId, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.Null(await Tokens(db).RotateRefreshTokenAsync(ana.RefreshToken, CancellationToken.None));
        Assert.NotNull(await Tokens(db).RotateRefreshTokenAsync(luis.RefreshToken, CancellationToken.None)); // control positivo
    }

    [Fact]
    public async Task DeleteRefreshTokensAsync_DoesNotCommitByItself()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Open(name);
        var ana = await StartSessionAsync(db);

        await Tokens(db).DeleteRefreshTokensAsync(ana.UserId, CancellationToken.None); // sin SaveChanges

        await using var other = Open(name);
        Assert.Equal(1, await other.RefreshTokens.CountAsync(t => t.UserId == ana.UserId));
    }
}

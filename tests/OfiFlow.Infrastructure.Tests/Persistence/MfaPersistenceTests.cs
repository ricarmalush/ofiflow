using OfiFlow.Application.Common.Abstractions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// La persistencia del segundo factor (spec 010, bloque 2) contra SQL Server real (ADR-008): lo que una base en memoria
/// no puede comprobar — los índices únicos, la columna de concurrencia, que el secreto cifrado sea lo que de verdad
/// queda en la columna, y que borrar una cuenta no deje filas huérfanas.
/// </summary>
public class MfaPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    // Estas tablas no son de una empresa: el contexto se abre con una cualquiera y no las filtra.
    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private static string UniqueHash() => Guid.NewGuid().ToString("N");

    // ===== Guardar y leer =====

    [Fact]
    public async Task AMfaCredential_SurvivesAFullRoundTrip_PendingAndThenConfirmed()
    {
        var userId = Guid.NewGuid();
        await using (var db = Open())
        {
            db.MfaCredentials.Add(MfaCredential.CreatePending(userId, "cifrado-1"));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        var loaded = await read.MfaCredentials.SingleAsync(c => c.UserId == userId);

        Assert.Equal("cifrado-1", loaded.SecretCipher);
        Assert.Null(loaded.ConfirmedAt);
        Assert.False(loaded.IsConfirmed); // recién pedido: pendiente
        Assert.Equal(0, loaded.LastUsedTimeStep);
    }

    [Fact]
    public async Task ARecoveryCode_AndAChallenge_SurviveARoundTrip()
    {
        var userId = Guid.NewGuid();
        var codeHash = UniqueHash();
        var tokenHash = UniqueHash();

        await using (var db = Open())
        {
            db.RecoveryCodes.Add(RecoveryCode.Create(userId, codeHash));
            db.MfaChallenges.Add(MfaChallenge.Create(userId, tokenHash, Now.AddMinutes(5), ChallengeAudience.Tenant));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        var code = await read.RecoveryCodes.SingleAsync(c => c.CodeHash == codeHash);
        var challenge = await read.MfaChallenges.SingleAsync(c => c.TokenHash == tokenHash);

        Assert.Equal(userId, code.UserId);
        Assert.Null(code.UsedAt);
        Assert.Equal(userId, challenge.UserId);
        Assert.Equal(Now.AddMinutes(5), challenge.ExpiresAt);
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Null(challenge.ConsumedAt);
    }

    // ===== Unicidad =====

    [Fact]
    public async Task AnAccount_CannotHaveTwoMfaCredentials()
    {
        var userId = Guid.NewGuid();
        await using (var db = Open())
        {
            db.MfaCredentials.Add(MfaCredential.CreatePending(userId, "uno"));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var second = Open();
        second.MfaCredentials.Add(MfaCredential.CreatePending(userId, "dos"));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwoAccounts_CanEachHaveTheirOwnMfaCredential()
    {
        // Control positivo del test anterior: el rechazo es por repetir la cuenta, no por la tabla.
        await using var db = Open();
        db.MfaCredentials.Add(MfaCredential.CreatePending(Guid.NewGuid(), "uno"));
        db.MfaCredentials.Add(MfaCredential.CreatePending(Guid.NewGuid(), "dos"));

        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TwoRecoveryCodes_CannotShareTheSameHash_EvenForDifferentAccounts()
    {
        var hash = UniqueHash();
        await using (var db = Open())
        {
            db.RecoveryCodes.Add(RecoveryCode.Create(Guid.NewGuid(), hash));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var second = Open();
        second.RecoveryCodes.Add(RecoveryCode.Create(Guid.NewGuid(), hash));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnAccount_CanHoldTenRecoveryCodes_WithDifferentHashes()
    {
        var userId = Guid.NewGuid();
        await using (var db = Open())
        {
            db.RecoveryCodes.AddRange(Enumerable.Range(0, 10).Select(_ => RecoveryCode.Create(userId, UniqueHash())));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        Assert.Equal(10, await read.RecoveryCodes.CountAsync(c => c.UserId == userId));
    }

    [Fact]
    public async Task TwoChallenges_CannotShareTheSameTokenHash()
    {
        var hash = UniqueHash();
        await using (var db = Open())
        {
            db.MfaChallenges.Add(MfaChallenge.Create(Guid.NewGuid(), hash, Now.AddMinutes(5), ChallengeAudience.Tenant));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var second = Open();
        second.MfaChallenges.Add(MfaChallenge.Create(Guid.NewGuid(), hash, Now.AddMinutes(5), ChallengeAudience.Tenant));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    // ===== Concurrencia: dos peticiones a la vez sobre la misma fila (R4, R9, R11) =====

    [Fact]
    public async Task TwoSimultaneousUpdates_OfTheSameCredential_OnlyOneWins()
    {
        // Dos peticiones con el mismo código TOTP leen el mismo último paso (0): las dos lo darían por bueno.
        var userId = Guid.NewGuid();
        await using (var seed = Open())
        {
            seed.MfaCredentials.Add(MfaCredential.CreatePending(userId, "cifrado"));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var first = Open();
        await using var second = Open();
        var seenByFirst = await first.MfaCredentials.SingleAsync(c => c.UserId == userId);
        var seenBySecond = await second.MfaCredentials.SingleAsync(c => c.UserId == userId);

        first.Entry(seenByFirst).Property(c => c.LastUsedTimeStep).CurrentValue = 100;
        second.Entry(seenBySecond).Property(c => c.LastUsedTimeStep).CurrentValue = 100;

        await first.SaveChangesAsync(CancellationToken.None); // gana la primera
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwoSimultaneousUses_OfTheSameRecoveryCode_OnlyOneWins()
    {
        var hash = UniqueHash();
        await using (var seed = Open())
        {
            seed.RecoveryCodes.Add(RecoveryCode.Create(Guid.NewGuid(), hash));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var first = Open();
        await using var second = Open();
        var seenByFirst = await first.RecoveryCodes.SingleAsync(c => c.CodeHash == hash);
        var seenBySecond = await second.RecoveryCodes.SingleAsync(c => c.CodeHash == hash);

        first.Entry(seenByFirst).Property(c => c.UsedAt).CurrentValue = Now;
        second.Entry(seenBySecond).Property(c => c.UsedAt).CurrentValue = Now;

        await first.SaveChangesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwoSimultaneousRedemptions_OfTheSameChallenge_OnlyOneWins()
    {
        var hash = UniqueHash();
        await using (var seed = Open())
        {
            seed.MfaChallenges.Add(MfaChallenge.Create(Guid.NewGuid(), hash, Now.AddMinutes(5), ChallengeAudience.Tenant));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var first = Open();
        await using var second = Open();
        var seenByFirst = await first.MfaChallenges.SingleAsync(c => c.TokenHash == hash);
        var seenBySecond = await second.MfaChallenges.SingleAsync(c => c.TokenHash == hash);

        first.Entry(seenByFirst).Property(c => c.ConsumedAt).CurrentValue = Now;
        second.Entry(seenBySecond).Property(c => c.ConsumedAt).CurrentValue = Now;

        await first.SaveChangesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(CancellationToken.None));
    }

    // ===== El secreto no queda en claro en la columna =====

    [Fact]
    public async Task TheSecret_StoredInTheDatabase_IsTheEncryptedValue_NeverThePlainSecret()
    {
        var userId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(20);
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));

        await using (var db = Open())
        {
            db.MfaCredentials.Add(MfaCredential.CreatePending(userId, protector.Protect(secret, userId.ToByteArray())));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Se lee la columna tal cual la guarda SQL Server, sin pasar por el modelo.
        await using var raw = Open();
        var column = await raw.Database
            .SqlQuery<string>($"SELECT SecretCipher AS Value FROM MfaCredentials WHERE UserId = {userId}")
            .SingleAsync();

        Assert.DoesNotContain(Convert.ToBase64String(secret), column);
        Assert.DoesNotContain(Convert.ToHexString(secret), column, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(secret, protector.Unprotect(column, userId.ToByteArray())); // y es recuperable con la clave
    }

    // ===== Borrar la cuenta no deja huérfanos (R20) =====

    private static async Task<Guid> SeedAccountWithMfaAsync(ApplicationDbContext db, string email)
    {
        var id = Guid.NewGuid();
        var created = await new IdentityService(db, NullLogger<IdentityService>.Instance)
            .CreateUserAsync(id, email, "Password123!", CancellationToken.None);
        Assert.True(created.Succeeded);

        db.MfaCredentials.Add(MfaCredential.CreatePending(id, "cifrado"));
        db.RecoveryCodes.AddRange(Enumerable.Range(0, 3).Select(_ => RecoveryCode.Create(id, UniqueHash())));
        db.MfaChallenges.Add(MfaChallenge.Create(id, UniqueHash(), Now.AddMinutes(5), ChallengeAudience.Tenant));
        await db.SaveChangesAsync(CancellationToken.None);

        return id;
    }

    [Fact]
    public async Task DeletingAnAccount_RemovesItsCredential_ItsRecoveryCodes_AndItsChallenges_AndNobodyElses()
    {
        Guid ana;
        Guid luis;
        await using (var seed = Open())
        {
            ana = await SeedAccountWithMfaAsync(seed, $"{Guid.NewGuid():N}@example.com");
            luis = await SeedAccountWithMfaAsync(seed, $"{Guid.NewGuid():N}@example.com");
        }

        await using (var db = Open())
        {
            await new IdentityService(db, NullLogger<IdentityService>.Instance).DeleteUserAsync(ana, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = Open();
        Assert.Equal(0, await read.ApplicationUsers.CountAsync(u => u.Id == ana));
        Assert.Equal(0, await read.MfaCredentials.CountAsync(c => c.UserId == ana));
        Assert.Equal(0, await read.RecoveryCodes.CountAsync(c => c.UserId == ana));
        Assert.Equal(0, await read.MfaChallenges.CountAsync(c => c.UserId == ana));

        // Control: Luis conserva todo lo suyo, así que el borrado no fue más ancho de lo debido.
        Assert.Equal(1, await read.ApplicationUsers.CountAsync(u => u.Id == luis));
        Assert.Equal(1, await read.MfaCredentials.CountAsync(c => c.UserId == luis));
        Assert.Equal(3, await read.RecoveryCodes.CountAsync(c => c.UserId == luis));
        Assert.Equal(1, await read.MfaChallenges.CountAsync(c => c.UserId == luis));
    }

    [Fact]
    public async Task TheMfaRows_AreNotDeleted_UntilTheHandlerConfirmsTheWholeRemoval()
    {
        // Como el resto de la baja (spec 009 R14), el borrado no confirma por su cuenta: si la transacción se
        // deshace, la cuenta y su segundo factor se quedan juntos.
        Guid ana;
        await using (var seed = Open())
        {
            ana = await SeedAccountWithMfaAsync(seed, $"{Guid.NewGuid():N}@example.com");
        }

        await using (var db = Open())
        {
            await new IdentityService(db, NullLogger<IdentityService>.Instance).DeleteUserAsync(ana, CancellationToken.None);
            // sin SaveChanges
        }

        await using var read = Open();
        Assert.Equal(1, await read.MfaCredentials.CountAsync(c => c.UserId == ana));
        Assert.Equal(3, await read.RecoveryCodes.CountAsync(c => c.UserId == ana));
    }
}

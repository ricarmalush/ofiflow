using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// La persistencia de las invitaciones (spec 009, bloque 2) contra SQL Server real (ADR-008): lo que
/// una base en memoria no puede comprobar, como los índices únicos, la columna de concurrencia o
/// que una transacción se deshaga entera.
/// </summary>
public class InvitationPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private static Invitation NewInvitation(Guid tenantId, string? hash = null, string email = "Ana@Example.com", TenantRole role = TenantRole.Technician) =>
        Invitation.Create(tenantId, Email.Create(email), role, hash ?? Guid.NewGuid().ToString("N"), Guid.NewGuid(), Now);

    private async Task<Invitation> SaveAsync(Invitation invitation)
    {
        await using var db = _fixture.CreateDbContext(invitation.TenantId);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(CancellationToken.None);
        return invitation;
    }

    // ===== El esquema =====

    [Fact]
    public async Task TheMigrations_MatchTheModel()
    {
        // Si alguien cambia una entidad y se olvida de generar la migración, la base de producción no
        // tendría la columna y fallaría en ejecución. Este test lo caza antes.
        await using var db = _fixture.CreateDbContext(Guid.NewGuid());

        Assert.False(db.Database.HasPendingModelChanges(),
            "El modelo de EF Core tiene cambios sin migración. Genera una con: dotnet ef migrations add <Nombre> --project src/OfiFlow.Infrastructure");
    }

    // ===== Guardar y leer =====

    [Fact]
    public async Task AnInvitation_SurvivesAFullRoundTrip()
    {
        var tenantId = Guid.NewGuid();
        var saved = await SaveAsync(NewInvitation(tenantId, hash: "hash-1", email: "Ana@Example.com", role: TenantRole.Manager));

        await using var db = _fixture.CreateDbContext(tenantId);
        var loaded = await db.Invitations.SingleAsync(i => i.Id == saved.Id);

        Assert.Equal(tenantId, loaded.TenantId);
        Assert.Equal("Ana@Example.com", loaded.Email.Value);
        Assert.Equal("ANA@EXAMPLE.COM", loaded.NormalizedEmail);
        Assert.Equal(TenantRole.Manager, loaded.Role);
        Assert.Equal("hash-1", loaded.TokenHash);
        Assert.Equal(Now.AddDays(7), loaded.ExpiresAt);
        Assert.Equal(saved.CreatedByUserId, loaded.CreatedByUserId);
        Assert.Equal(InvitationStatus.Pending, loaded.StatusAt(Now));
    }

    [Fact]
    public async Task TheNormalizedEmail_FindsTheInvitationWhateverTheCase()
    {
        var tenantId = Guid.NewGuid();
        await SaveAsync(NewInvitation(tenantId, email: "Ana@Example.com"));

        await using var db = _fixture.CreateDbContext(tenantId);
        var normalized = Email.Create("ANA@example.COM").Value.ToUpperInvariant();

        Assert.Single(await db.Invitations.Where(i => i.NormalizedEmail == normalized).ToListAsync());
    }

    // ===== El token es único =====

    [Fact]
    public async Task TwoInvitations_CannotShareTheSameTokenHash()
    {
        var hash = Guid.NewGuid().ToString("N");
        await SaveAsync(NewInvitation(Guid.NewGuid(), hash));

        // Ni siquiera desde otra empresa: el token se busca sin saber la empresa, así que sería ambiguo.
        await using var db = _fixture.CreateDbContext(Guid.NewGuid());
        db.Invitations.Add(NewInvitation(Guid.NewGuid(), hash));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvitationsWithDifferentHashes_CanCoexist()
    {
        // Control positivo del test anterior: el rechazo se debe al hash repetido y no a otra cosa.
        var tenantId = Guid.NewGuid();
        await SaveAsync(NewInvitation(tenantId));
        await SaveAsync(NewInvitation(tenantId));

        await using var db = _fixture.CreateDbContext(tenantId);
        Assert.Equal(2, await db.Invitations.CountAsync());
    }

    // ===== Concurrencia: dos aceptaciones del mismo token (R8) =====

    [Fact]
    public async Task TwoSimultaneousAcceptances_OnlyOneWins()
    {
        var tenantId = Guid.NewGuid();
        var saved = await SaveAsync(NewInvitation(tenantId));

        // Dos peticiones leen la invitación a la vez: las dos la ven pendiente.
        await using var first = _fixture.CreateDbContext(tenantId);
        await using var second = _fixture.CreateDbContext(tenantId);
        var seenByFirst = await first.Invitations.SingleAsync(i => i.Id == saved.Id);
        var seenBySecond = await second.Invitations.SingleAsync(i => i.Id == saved.Id);
        Assert.True(seenByFirst.IsPendingAt(Now.AddHours(1)));
        Assert.True(seenBySecond.IsPendingAt(Now.AddHours(1)));

        seenByFirst.Accept(Now.AddHours(1));
        await first.SaveChangesAsync(CancellationToken.None);

        // La segunda también cree que puede aceptarla; al guardar, la base de datos se lo impide.
        seenBySecond.Accept(Now.AddHours(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(CancellationToken.None));

        await using var check = _fixture.CreateDbContext(tenantId);
        var final = await check.Invitations.SingleAsync(i => i.Id == saved.Id);
        Assert.Equal(Now.AddHours(1), final.AcceptedAt);
    }

    [Fact]
    public async Task AcceptingAfterTheOtherOneFinished_IsRejectedByTheDomain_NotByTheDatabase()
    {
        // Control positivo: si la segunda petición lee DESPUÉS, ya la ve aceptada y el dominio la rechaza.
        var tenantId = Guid.NewGuid();
        var saved = await SaveAsync(NewInvitation(tenantId));

        await using (var first = _fixture.CreateDbContext(tenantId))
        {
            var invitation = await first.Invitations.SingleAsync(i => i.Id == saved.Id);
            invitation.Accept(Now.AddHours(1));
            await first.SaveChangesAsync(CancellationToken.None);
        }

        await using var second = _fixture.CreateDbContext(tenantId);
        var late = await second.Invitations.SingleAsync(i => i.Id == saved.Id);

        var exception = Assert.Throws<DomainException>(() => late.Accept(Now.AddHours(2)));
        Assert.Equal(InvitationErrors.Invalid, exception.Code);
    }

    // ===== Una baja es todo o nada (R14) =====

    [Fact]
    public async Task ARemoval_IsAllOrNothing_IfAnyPartFailsNothingIsDeleted()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var jwt = new JwtOptions
        {
            Secret = Convert.ToBase64String(new byte[32]),
            Issuer = "OfiFlow.Tests",
            Audience = "OfiFlow.Tests",
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7
        };

        // Un miembro con su cuenta, su ficha, su pertenencia y una sesión abierta.
        await using (var seed = _fixture.CreateDbContext(tenantId))
        {
            var identity = new IdentityService(seed, NullLogger<IdentityService>.Instance);
            Assert.True((await identity.CreateUserAsync(userId, $"{userId:N}@example.com", "Password123!", CancellationToken.None)).Succeeded);
            seed.Users.Add(User.Create(userId, "Ana", Email.Create($"{userId:N}@example.com")));
            seed.TenantUsers.Add(TenantUser.Create(tenantId, userId, TenantRole.Technician));
            await seed.SaveChangesAsync(CancellationToken.None);

            var tokens = new TokenService(seed, Microsoft.Extensions.Options.Options.Create(jwt), NullLogger<TokenService>.Instance);
            await tokens.IssueTokensAsync(userId, tenantId, TenantRole.Technician, CancellationToken.None);
        }

        var invitation = await SaveAsync(NewInvitation(tenantId));

        // La baja lee la invitación... y mientras tanto otra petición la modifica.
        await using var removal = _fixture.CreateDbContext(tenantId);
        var stale = await removal.Invitations.SingleAsync(i => i.Id == invitation.Id);

        await using (var other = _fixture.CreateDbContext(tenantId))
        {
            var concurrent = await other.Invitations.SingleAsync(i => i.Id == invitation.Id);
            concurrent.Revoke(Now.AddHours(1));
            await other.SaveChangesAsync(CancellationToken.None);
        }

        // La baja intenta borrar todo y además tocar la invitación desfasada: la base lo rechaza entero.
        var identityForRemoval = new IdentityService(removal, NullLogger<IdentityService>.Instance);
        var tokensForRemoval = new TokenService(removal, Microsoft.Extensions.Options.Options.Create(jwt), NullLogger<TokenService>.Instance);
        await identityForRemoval.DeleteUserAsync(userId, CancellationToken.None);
        await tokensForRemoval.DeleteRefreshTokensAsync(userId, CancellationToken.None);
        removal.TenantUsers.Remove(await removal.TenantUsers.SingleAsync(tu => tu.UserId == userId));
        removal.Users.Remove(await removal.Users.SingleAsync(u => u.Id == userId));
        stale.Revoke(Now.AddHours(2)); // desfasada: ya la revocó la otra petición... pero esta cree que sigue pendiente

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => removal.SaveChangesAsync(CancellationToken.None));

        // Nada se ha borrado: ni la cuenta, ni la ficha, ni la pertenencia, ni la sesión.
        await using var check = _fixture.CreateDbContext(tenantId);
        Assert.Equal(1, await check.ApplicationUsers.CountAsync(u => u.Id == userId));
        Assert.Equal(1, await check.Users.CountAsync(u => u.Id == userId));
        Assert.Equal(1, await check.TenantUsers.CountAsync(tu => tu.UserId == userId));
        Assert.Equal(1, await check.RefreshTokens.CountAsync(rt => rt.UserId == userId));
    }

    [Fact]
    public async Task ARemoval_WhenEverythingGoesWell_LeavesNoTraceOfTheMember()
    {
        // Control positivo del anterior: sin conflicto, la misma baja sí lo borra todo, y no deja huérfanas.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var jwt = new JwtOptions
        {
            Secret = Convert.ToBase64String(new byte[32]),
            Issuer = "OfiFlow.Tests",
            Audience = "OfiFlow.Tests",
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7
        };

        await using (var seed = _fixture.CreateDbContext(tenantId))
        {
            var identity = new IdentityService(seed, NullLogger<IdentityService>.Instance);
            Assert.True((await identity.CreateUserAsync(userId, $"{userId:N}@example.com", "Password123!", CancellationToken.None)).Succeeded);
            seed.Users.Add(User.Create(userId, "Ana", Email.Create($"{userId:N}@example.com")));
            seed.TenantUsers.Add(TenantUser.Create(tenantId, userId, TenantRole.Technician));
            await seed.SaveChangesAsync(CancellationToken.None);

            var tokens = new TokenService(seed, Microsoft.Extensions.Options.Options.Create(jwt), NullLogger<TokenService>.Instance);
            var issued = await tokens.IssueTokensAsync(userId, tenantId, TenantRole.Technician, CancellationToken.None);
            await tokens.RotateRefreshTokenAsync(issued.RefreshToken, CancellationToken.None); // deja un token revocado y otro activo
        }

        await using (var removal = _fixture.CreateDbContext(tenantId))
        {
            await new IdentityService(removal, NullLogger<IdentityService>.Instance).DeleteUserAsync(userId, CancellationToken.None);
            await new TokenService(removal, Microsoft.Extensions.Options.Options.Create(jwt), NullLogger<TokenService>.Instance)
                .DeleteRefreshTokensAsync(userId, CancellationToken.None);
            removal.TenantUsers.Remove(await removal.TenantUsers.SingleAsync(tu => tu.UserId == userId));
            removal.Users.Remove(await removal.Users.SingleAsync(u => u.Id == userId));
            await removal.SaveChangesAsync(CancellationToken.None);
        }

        await using var check = _fixture.CreateDbContext(tenantId);
        Assert.Equal(0, await check.ApplicationUsers.CountAsync(u => u.Id == userId));
        Assert.Equal(0, await check.Users.CountAsync(u => u.Id == userId));
        Assert.Equal(0, await check.TenantUsers.CountAsync(tu => tu.UserId == userId));
        Assert.Equal(0, await check.RefreshTokens.CountAsync(rt => rt.UserId == userId)); // ni siquiera los revocados
    }
}

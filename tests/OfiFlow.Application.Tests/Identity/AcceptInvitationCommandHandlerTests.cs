using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity.Commands.AcceptInvitation;
using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

public class AcceptInvitationCommandHandlerTests
{
    private const string Password = "Password123!";
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeIdentityService _identity = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ListLogger<AcceptInvitationCommandHandler> _logger = new();

    private AcceptInvitationCommandHandler Handler() => new(_db, _identity, _time, _logger);

    /// <summary>Siembra una invitación y devuelve el token en claro (el que le llegaría a la persona).</summary>
    private async Task<(Invitation Invitation, string Token)> SeedAsync(
        string email = "Ana@Example.com", TenantRole role = TenantRole.Technician, DateTime? issuedAt = null)
    {
        var (token, hash) = InvitationToken.Generate();
        var invitation = Invitation.Create(_tenantId, Email.Create(email), role, hash, Guid.NewGuid(), issuedAt ?? Now);
        _db.Invitations.Add(invitation);
        await _db.SaveChangesAsync(CancellationToken.None);
        return (invitation, token);
    }

    private Task AcceptAsync(string token, string name = "Ana Pérez", string password = Password) =>
        Handler().Handle(new AcceptInvitationCommand(token, name, password), CancellationToken.None);

    // ===== Camino feliz =====

    [Fact]
    public async Task AcceptingCreatesTheAccount_TheProfileAndTheMembership_WithTheRoleAndTenantOfTheInvitation()
    {
        var (invitation, token) = await SeedAsync("Ana@Example.com", TenantRole.Manager);

        await AcceptAsync(token, "Ana Pérez");

        var tenantUser = await _db.TenantUsers.SingleAsync();
        var user = await _db.Users.SingleAsync();

        Assert.Equal(_tenantId, tenantUser.TenantId); // la empresa de la invitación, no otra
        Assert.Equal(TenantRole.Manager, tenantUser.Role); // el rol que fijó quien invitó
        Assert.Equal(user.Id, tenantUser.UserId);
        Assert.Equal("Ana Pérez", user.Name);
        Assert.Equal("Ana@Example.com", user.ContactEmail.Value); // el email lo fijó quien invitó
        Assert.Equal(Now, (await _db.Invitations.SingleAsync(i => i.Id == invitation.Id)).AcceptedAt);
    }

    [Fact]
    public async Task ThePersonCanThenLogInWithTheirOwnPassword()
    {
        var (_, token) = await SeedAsync("Ana@Example.com");

        await AcceptAsync(token, password: "Mi-propia-clave-1");

        var user = await _db.Users.SingleAsync();
        Assert.Equal(user.Id, await _identity.AuthenticateAsync("Ana@Example.com", "Mi-propia-clave-1", CancellationToken.None));
        Assert.Null(await _identity.AuthenticateAsync("Ana@Example.com", "otra", CancellationToken.None));
    }

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public async Task EveryGrantableRole_ArrivesAsInvited(TenantRole role)
    {
        var (_, token) = await SeedAsync(role: role);

        await AcceptAsync(token);

        Assert.Equal(role, (await _db.TenantUsers.SingleAsync()).Role);
    }

    // ===== Un solo uso (R3) =====

    [Fact]
    public async Task TheSameTokenCannotBeUsedTwice_AndOnlyOneMembershipExists()
    {
        var (_, token) = await SeedAsync();
        await AcceptAsync(token);

        var exception = await Assert.ThrowsAsync<DomainException>(() => AcceptAsync(token, "Otra Persona", "Otra-clave-123"));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Equal(1, await _db.TenantUsers.CountAsync());
        Assert.Equal(1, await _db.Users.CountAsync());
    }

    // ===== La misma respuesta para los cuatro fallos (R7) =====

    [Fact]
    public async Task UnknownExpiredRevokedAndUsedTokens_AllGiveTheSameCode_AndCreateNothing()
    {
        var unknown = InvitationToken.Generate().Value; // bien formado, pero no existe

        var (_, expiredToken) = await SeedAsync("caducada@example.com", issuedAt: Now.AddDays(-8));

        var (revoked, revokedToken) = await SeedAsync("revocada@example.com");
        revoked.Revoke(Now);
        await _db.SaveChangesAsync(CancellationToken.None);

        var (_, usedToken) = await SeedAsync("usada@example.com");
        await AcceptAsync(usedToken);
        var accountsBefore = await _db.Users.CountAsync();

        var codes = new List<string>();
        foreach (var token in new[] { unknown, expiredToken, revokedToken, usedToken })
        {
            var exception = await Assert.ThrowsAsync<DomainException>(() => AcceptAsync(token));
            codes.Add(exception.Code);
            Assert.Empty(exception.Arguments); // ni siquiera el motivo va en los argumentos
        }

        Assert.All(codes, code => Assert.Equal(InvitationErrors.Invalid, code));
        Assert.Equal(accountsBefore, await _db.Users.CountAsync()); // los cuatro intentos no crearon nada
    }

    [Fact]
    public async Task ARejectedAttempt_LogsTheRealReason_ButNeverTheTokenOrTheEmail()
    {
        var (revoked, token) = await SeedAsync("ana.secreta@example.com");
        revoked.Revoke(Now);
        await _db.SaveChangesAsync(CancellationToken.None);

        await Assert.ThrowsAsync<DomainException>(() => AcceptAsync(token));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationRejected);
        Assert.Contains("Revoked", entry.Message); // el analista sí ve el motivo real
        Assert.Contains(revoked.Id.ToString(), entry.Message);
        Assert.DoesNotContain(token, entry.Message);
        Assert.DoesNotContain(InvitationToken.Hash(token), entry.Message);
        Assert.DoesNotContain("ana.secreta@example.com", entry.Message);
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Unknown")]
    public async Task TheLoggedReason_DistinguishesExpiredFromUnknown(string expected)
    {
        var token = expected == "Unknown"
            ? InvitationToken.Generate().Value
            : (await SeedAsync(issuedAt: Now.AddDays(-8))).Token;

        await Assert.ThrowsAsync<DomainException>(() => AcceptAsync(token));

        Assert.Contains(expected, Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationRejected).Message);
    }

    // ===== El token no se consume si algo falla (R4, R6) =====

    [Fact]
    public async Task IfTheEmailGotRegisteredInTheMeantime_ItFails_AndTheInvitationIsNotConsumed()
    {
        var (invitation, token) = await SeedAsync("Ana@Example.com");
        _identity.AddExistingAccount("Ana@Example.com"); // alguien se registró con ese email mientras tanto

        var exception = await Assert.ThrowsAsync<IdentityOperationException>(() => AcceptAsync(token));

        Assert.Contains(IdentityErrors.EmailAlreadyRegistered, exception.Codes);
        Assert.Null((await _db.Invitations.SingleAsync(i => i.Id == invitation.Id)).AcceptedAt);
        Assert.False(await _db.TenantUsers.AnyAsync());
        Assert.False(await _db.Users.AnyAsync());
    }

    // ===== Dos aceptaciones simultáneas (R8) =====

    [Fact]
    public async Task IfTheDatabaseDetectsAConcurrentAcceptance_TheLoserGetsTheSameInvalidCode()
    {
        var (_, token) = await SeedAsync();
        _db.ExceptionOnSave = new DbUpdateConcurrencyException("otra petición ganó");

        var exception = await Assert.ThrowsAsync<DomainException>(() => AcceptAsync(token));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Contains("Concurrent", Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationRejected).Message);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationAccepted);
    }

    [Fact]
    public async Task IfTheDatabaseRejectsTheSaveWithADuplicateKey_ItIsTheSameInvalidCode_NotAServerError()
    {
        // La otra forma en que la base rechaza a la perdedora de una carrera: el índice único del email (dos cuentas
        // con el mismo email). Antes de capturarla, esta petición acababa en un 500.
        var (_, token) = await SeedAsync();
        _db.ExceptionOnSave = new DbUpdateException("clave duplicada");

        var exception = await Assert.ThrowsAsync<DomainException>(() => AcceptAsync(token));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Contains("Concurrent", Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationRejected).Message);
    }

    // ===== Evento de seguridad (ADR-009 R5) =====

    [Fact]
    public async Task AnAcceptance_IsLogged_WithIdsAndRole_ButNeverThePasswordTheTokenOrTheEmail()
    {
        var (invitation, token) = await SeedAsync("ana.secreta@example.com", TenantRole.Technician);

        await AcceptAsync(token, "Ana Pérez", "Clave-secreta-123");

        var userId = (await _db.Users.SingleAsync()).Id;
        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationAccepted);
        Assert.Contains(invitation.Id.ToString(), entry.Message);
        Assert.Contains(_tenantId.ToString(), entry.Message);
        Assert.Contains(userId.ToString(), entry.Message);
        Assert.Contains("Technician", entry.Message);
        Assert.DoesNotContain("Clave-secreta-123", entry.Message);
        Assert.DoesNotContain(token, entry.Message);
        Assert.DoesNotContain("ana.secreta@example.com", entry.Message);
        Assert.DoesNotContain("Ana Pérez", entry.Message);
    }
}

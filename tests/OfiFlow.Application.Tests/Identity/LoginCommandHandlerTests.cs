using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

public class LoginCommandHandlerTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeIdentityService _identity = new();
    private readonly FakeTokenService _tokens = new();
    private readonly FakeMfaService _mfa = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly TestDbContext _db = TestDbContextFactory.Create();

    private LoginCommandHandler Handler() =>
        new(_db, _identity, _tokens, _mfa, _clock, NullLogger<LoginCommandHandler>.Instance);

    private async Task<(Guid UserId, Guid TenantId)> SeedAccountAsync(string email = "juan@example.com", TenantRole role = TenantRole.Owner)
    {
        var userId = Guid.NewGuid();
        await _identity.CreateUserAsync(userId, email, "Password123!", CancellationToken.None);

        var tenant = Tenant.Create("Fontanería Pérez");
        _db.Tenants.Add(tenant);
        _db.TenantUsers.Add(TenantUser.Create(tenant.Id, userId, role));
        await _db.SaveChangesAsync(CancellationToken.None);

        return (userId, tenant.Id);
    }

    private Task<LoginResult> LoginAsync(string email = "juan@example.com", string password = "Password123!") =>
        Handler().Handle(new LoginCommand(email, password), CancellationToken.None);

    // ===== Sin segundo factor: como siempre =====

    [Fact]
    public async Task Handle_WithValidCredentialsAndTenantMembership_ReturnsTheSession()
    {
        var (userId, tenantId) = await SeedAccountAsync();

        var result = await LoginAsync();

        Assert.IsType<LoginResult.Authenticated>(result);
        Assert.Equal([(userId, tenantId, TenantRole.Owner)], _tokens.Issued);
        Assert.Empty(_mfa.CreatedChallenges); // sin MFA no hay desafío
    }

    [Fact]
    public async Task Handle_WithInvalidCredentials_IsRejected()
    {
        var result = await LoginAsync("nadie@example.com", "incorrecta");

        Assert.IsType<LoginResult.Rejected>(result);
        Assert.Equal(0, _tokens.IssuedSessions);
    }

    [Fact]
    public async Task Handle_WithValidCredentialsButNoTenantMembership_IsRejected()
    {
        await _identity.CreateUserAsync(Guid.NewGuid(), "huerfano@example.com", "Password123!", CancellationToken.None);

        var result = await LoginAsync("huerfano@example.com");

        Assert.IsType<LoginResult.Rejected>(result);
        Assert.Equal(0, _tokens.IssuedSessions);
    }

    // ===== Con segundo factor: la contraseña NO da sesión (R10, R12) =====

    [Fact]
    public async Task AnAccountWithMfa_AfterTheCorrectPassword_GetsAChallenge_AndNoSessionAtAll()
    {
        var (userId, _) = await SeedAccountAsync();
        _identity.AccountsWithSecondFactor.Add(userId);

        var result = await LoginAsync();

        var required = Assert.IsType<LoginResult.SecondFactorRequired>(result);
        Assert.NotEmpty(required.Challenge.Challenge);

        // El corazón de R12: ni un solo token se emitió.
        Assert.Equal(0, _tokens.IssuedSessions);
        Assert.Empty(_tokens.Issued);
    }

    [Fact]
    public async Task TheChallenge_IsAnOpaqueToken_AndOnlyItsHashIsStored()
    {
        var (userId, _) = await SeedAccountAsync();
        _identity.AccountsWithSecondFactor.Add(userId);

        var required = Assert.IsType<LoginResult.SecondFactorRequired>(await LoginAsync());

        var stored = Assert.Single(_mfa.CreatedChallenges);
        Assert.Equal(userId, stored.UserId);
        Assert.True(OpaqueToken.IsWellFormed(required.Challenge.Challenge)); // 43 caracteres de URL, 256 bits
        Assert.Equal(OpaqueToken.Hash(required.Challenge.Challenge), stored.Hash);
        Assert.NotEqual(required.Challenge.Challenge, stored.Hash); // lo guardado no es el valor entregado
    }

    [Fact]
    public async Task TheChallenge_ExpiresInFiveMinutes_AndTheResponseSaysWhen()
    {
        var (userId, _) = await SeedAccountAsync();
        _identity.AccountsWithSecondFactor.Add(userId);

        var required = Assert.IsType<LoginResult.SecondFactorRequired>(await LoginAsync());

        Assert.Equal(Start.AddMinutes(5), required.Challenge.ExpiresAt);
        Assert.Equal(Start.AddMinutes(5), Assert.Single(_mfa.CreatedChallenges).ExpiresAt);
    }

    [Fact]
    public async Task EveryLogin_GetsADifferentChallenge()
    {
        var (userId, _) = await SeedAccountAsync();
        _identity.AccountsWithSecondFactor.Add(userId);

        var first = Assert.IsType<LoginResult.SecondFactorRequired>(await LoginAsync());
        var second = Assert.IsType<LoginResult.SecondFactorRequired>(await LoginAsync());

        Assert.NotEqual(first.Challenge.Challenge, second.Challenge.Challenge);
    }

    [Fact]
    public async Task AnAccountWithMfa_WithAWrongPassword_GetsNoChallenge()
    {
        var (userId, _) = await SeedAccountAsync();
        _identity.AccountsWithSecondFactor.Add(userId);

        var result = await LoginAsync(password: "incorrecta");

        Assert.IsType<LoginResult.Rejected>(result);
        Assert.Empty(_mfa.CreatedChallenges); // la contraseña mala no da ni un desafío
    }

    [Fact]
    public async Task AnAccountWithMfaButNoMembership_GetsNoChallenge()
    {
        var userId = Guid.NewGuid();
        await _identity.CreateUserAsync(userId, "huerfano@example.com", "Password123!", CancellationToken.None);
        _identity.AccountsWithSecondFactor.Add(userId);

        var result = await LoginAsync("huerfano@example.com");

        Assert.IsType<LoginResult.Rejected>(result);
        Assert.Empty(_mfa.CreatedChallenges);
        Assert.Equal(0, _tokens.IssuedSessions);
    }

    // ===== Empresa suspendida (spec 011 R15a) =====

    private async Task SuspendTenantAsync(Guid tenantId)
    {
        var tenant = await _db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.Suspend(Start);
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_WithTheCorrectPassword_IsForbidden_AndNoSessionIsIssued()
    {
        var (_, tenantId) = await SeedAccountAsync();
        await SuspendTenantAsync(tenantId);

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => LoginAsync());

        Assert.Equal(TenancyErrors.TenantSuspended, denied.Code);
        Assert.Equal(0, _tokens.IssuedSessions);
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_WithMfa_GetsNoChallenge()
    {
        // Sin sesión posible no hay segundo paso: se corta ANTES de crear el desafío, no después de gastarlo.
        var (userId, tenantId) = await SeedAccountAsync();
        _identity.AccountsWithSecondFactor.Add(userId);
        await SuspendTenantAsync(tenantId);

        var denied = await Assert.ThrowsAsync<ForbiddenException>(() => LoginAsync());

        Assert.Equal(TenancyErrors.TenantSuspended, denied.Code);
        Assert.Empty(_mfa.CreatedChallenges);
        Assert.Equal(0, _tokens.IssuedSessions);
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_WithAWrongPassword_GetsTheSameRejectionAsAnyone()
    {
        // Sin las credenciales correctas no se revela que la empresa existe ni que está suspendida.
        var (_, tenantId) = await SeedAccountAsync();
        await SuspendTenantAsync(tenantId);

        var result = await LoginAsync(password: "incorrecta");

        Assert.IsType<LoginResult.Rejected>(result);
    }

    [Fact]
    public async Task SuspendingOneCompany_DoesNotStopTheMemberOfAnother()
    {
        var (_, suspendedTenant) = await SeedAccountAsync("a@example.com");
        var (userOfB, tenantOfB) = await SeedAccountAsync("b@example.com");
        await SuspendTenantAsync(suspendedTenant);

        var result = await LoginAsync("b@example.com");

        Assert.IsType<LoginResult.Authenticated>(result);
        Assert.Equal([(userOfB, tenantOfB, TenantRole.Owner)], _tokens.Issued);
    }

    [Fact]
    public async Task AfterReactivatingTheCompany_TheMemberLogsInAgain()
    {
        var (_, tenantId) = await SeedAccountAsync();
        await SuspendTenantAsync(tenantId);
        await Assert.ThrowsAsync<ForbiddenException>(() => LoginAsync());

        var tenant = await _db.Tenants.SingleAsync();
        tenant.Reactivate();
        await _db.SaveChangesAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Authenticated>(await LoginAsync());
    }
}

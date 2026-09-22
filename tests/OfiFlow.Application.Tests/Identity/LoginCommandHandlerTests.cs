using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

public class LoginCommandHandlerTests
{
    [Fact]
    public async Task Handle_WithValidCredentialsAndTenantMembership_ReturnsTokens()
    {
        await using var db = TestDbContextFactory.Create();
        var identityService = new FakeIdentityService();
        var tokenService = new FakeTokenService();

        var userId = Guid.NewGuid();
        await identityService.CreateUserAsync(userId, "juan@example.com", "Password123!", CancellationToken.None);

        var tenant = Tenant.Create("Fontanería Pérez");
        db.Tenants.Add(tenant);
        db.TenantUsers.Add(TenantUser.CreateOwner(tenant.Id, userId));
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new LoginCommandHandler(db, identityService, tokenService);
        var result = await handler.Handle(new LoginCommand("juan@example.com", "Password123!"), CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Handle_WithInvalidCredentials_ReturnsNull()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new LoginCommandHandler(db, new FakeIdentityService(), new FakeTokenService());

        var result = await handler.Handle(new LoginCommand("nadie@example.com", "incorrecta"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Handle_WithValidCredentialsButNoTenantMembership_ReturnsNull()
    {
        await using var db = TestDbContextFactory.Create();
        var identityService = new FakeIdentityService();
        var userId = Guid.NewGuid();
        await identityService.CreateUserAsync(userId, "huerfano@example.com", "Password123!", CancellationToken.None);

        var handler = new LoginCommandHandler(db, identityService, new FakeTokenService());
        var result = await handler.Handle(new LoginCommand("huerfano@example.com", "Password123!"), CancellationToken.None);

        Assert.Null(result);
    }
}

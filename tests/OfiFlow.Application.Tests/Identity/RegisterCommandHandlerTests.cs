using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Identity.Commands.Register;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

public class RegisterCommandHandlerTests
{
    [Fact]
    public async Task Handle_CreatesTenantUserAndOwnerTenantUser()
    {
        await using var db = TestDbContextFactory.Create();
        var identityService = new FakeIdentityService();
        var handler = new RegisterCommandHandler(db, identityService);

        var tenantId = await handler.Handle(
            new RegisterCommand("Fontanería Pérez", "Juan Pérez", "juan@example.com", "Password123!"),
            CancellationToken.None);

        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
        var tenantUser = await db.TenantUsers.FirstAsync(tu => tu.TenantId == tenantId);
        var user = await db.Users.FirstAsync(u => u.Id == tenantUser.UserId);

        Assert.Equal("Fontanería Pérez", tenant.Name);
        Assert.Equal(TenantRole.Owner, tenantUser.Role);
        Assert.Equal("Juan Pérez", user.Name);
        Assert.Equal("juan@example.com", user.ContactEmail.Value);
    }

    [Fact]
    public async Task Handle_WhenEmailAlreadyRegistered_ThrowsAndPersistsNothing()
    {
        await using var db = TestDbContextFactory.Create();
        var identityService = new FakeIdentityService { ShouldFailCreation = true };
        var handler = new RegisterCommandHandler(db, identityService);

        var exception = await Assert.ThrowsAsync<IdentityOperationException>(() =>
            handler.Handle(new RegisterCommand("Empresa", "Juan", "juan@example.com", "Password123!"), CancellationToken.None));

        Assert.Contains(IdentityErrors.EmailAlreadyRegistered, exception.Codes);

        Assert.False(await db.Tenants.AnyAsync());
    }
}

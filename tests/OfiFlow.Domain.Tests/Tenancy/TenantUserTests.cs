using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

public class TenantUserTests
{
    [Fact]
    public void CreateOwner_AssignsOwnerRole()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenantUser = TenantUser.CreateOwner(tenantId, userId);

        Assert.Equal(tenantId, tenantUser.TenantId);
        Assert.Equal(userId, tenantUser.UserId);
        Assert.Equal(TenantRole.Owner, tenantUser.Role);
    }

    [Fact]
    public void Create_WithExplicitRole_AssignsThatRole()
    {
        var tenantUser = TenantUser.Create(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Technician);

        Assert.Equal(TenantRole.Technician, tenantUser.Role);
    }
}

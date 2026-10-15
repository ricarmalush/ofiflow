using OfiFlow.Domain.Common;
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

    [Theory]
    [InlineData(TenantRole.Technician, TenantRole.Manager)]
    [InlineData(TenantRole.Employee, TenantRole.Admin)]
    [InlineData(TenantRole.Admin, TenantRole.Employee)]
    [InlineData(TenantRole.Manager, TenantRole.Manager)] // el mismo rol: no falla, no cambia nada
    public void ChangeRole_ChangesTheRole(TenantRole current, TenantRole newRole)
    {
        var tenantUser = TenantUser.Create(Guid.NewGuid(), Guid.NewGuid(), current);

        tenantUser.ChangeRole(newRole);

        Assert.Equal(newRole, tenantUser.Role);
    }

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public void ChangeRole_NeverTurnsAnyoneIntoOwner(TenantRole current)
    {
        var tenantUser = TenantUser.Create(Guid.NewGuid(), Guid.NewGuid(), current);

        var exception = Assert.Throws<DomainException>(() => tenantUser.ChangeRole(TenantRole.Owner));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        Assert.Equal(current, tenantUser.Role); // sin cambios
    }

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Technician)]
    public void ChangeRole_TheOwnerCannotChangeRole(TenantRole newRole)
    {
        // R12: el Owner es único por empresa y no se cambia, ni siquiera a un rol "menor".
        var owner = TenantUser.CreateOwner(Guid.NewGuid(), Guid.NewGuid());

        var exception = Assert.Throws<DomainException>(() => owner.ChangeRole(newRole));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
        Assert.Equal(TenantRole.Owner, owner.Role);
    }

    [Fact]
    public void ChangeRole_RejectsAValueThatIsNotARole()
    {
        var tenantUser = TenantUser.Create(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Employee);

        Assert.Throws<DomainException>(() => tenantUser.ChangeRole((TenantRole)999));
        Assert.Equal(TenantRole.Employee, tenantUser.Role);
    }
}

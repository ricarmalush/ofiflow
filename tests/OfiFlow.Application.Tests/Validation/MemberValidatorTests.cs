using FluentValidation.TestHelper;
using OfiFlow.Application.Tenancy.Commands.ChangeUserRole;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>ADR-009 R3: la entrada de "cambiar rol" se valida antes de llegar al handler.</summary>
public class MemberValidatorTests
{
    private readonly ChangeUserRoleCommandValidator _changeRole = new();

    [Fact]
    public void ChangeRole_AValidCommand_Passes()
    {
        _changeRole.TestValidate(new ChangeUserRoleCommand(Guid.NewGuid(), TenantRole.Manager)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ChangeRole_AnEmptyId_IsRejected()
    {
        _changeRole.TestValidate(new ChangeUserRoleCommand(Guid.Empty, TenantRole.Manager)).ShouldHaveValidationErrorFor(x => x.TenantUserId);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public void ChangeRole_ARoleThatDoesNotExist_IsRejected(int role)
    {
        _changeRole.TestValidate(new ChangeUserRoleCommand(Guid.NewGuid(), (TenantRole)role)).ShouldHaveValidationErrorFor(x => x.NewRole);
    }
}

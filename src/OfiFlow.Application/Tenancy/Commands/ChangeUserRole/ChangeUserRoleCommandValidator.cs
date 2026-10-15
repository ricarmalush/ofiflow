using FluentValidation;

namespace OfiFlow.Application.Tenancy.Commands.ChangeUserRole;

public sealed class ChangeUserRoleCommandValidator : AbstractValidator<ChangeUserRoleCommand>
{
    public ChangeUserRoleCommandValidator()
    {
        RuleFor(x => x.TenantUserId).NotEmpty();

        // Que sea un rol de verdad (no un 999). Que se pueda conceder, y por quién, lo deciden ManageableRoles y Domain.
        RuleFor(x => x.NewRole).IsInEnum();
    }
}

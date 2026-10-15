using FluentValidation;
using OfiFlow.Application.Common.Validation;

namespace OfiFlow.Application.Tenancy.Commands.InviteUser;

public sealed class InviteUserCommandValidator : AbstractValidator<InviteUserCommand>
{
    public InviteUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().ValidEmail();

        // Que el valor sea un rol de verdad (no un 999). Que ESE rol se pueda conceder lo decide ManageableRoles
        // en el handler: depende de quién invita.
        RuleFor(x => x.Role).IsInEnum();
    }
}

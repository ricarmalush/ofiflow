using FluentValidation;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.Register;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(User.NameMaxLength);
        RuleFor(x => x.Email).NotEmpty().ValidEmail();
        RuleFor(x => x.Password).NotEmpty().Length(PasswordRules.MinLength, PasswordRules.MaxLength);
    }
}

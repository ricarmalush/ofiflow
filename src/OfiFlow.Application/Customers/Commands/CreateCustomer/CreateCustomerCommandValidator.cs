using FluentValidation;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Customers.Commands.CreateCustomer;

public sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Customer.NameMaxLength);

        RuleFor(x => x.Email)
            .ValidEmail()
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        RuleFor(x => x.Phone)
            .ValidPhone()
            .When(x => !string.IsNullOrWhiteSpace(x.Phone));

        RuleFor(x => x.Address).MaximumLength(Customer.AddressMaxLength);
        RuleFor(x => x.Notes).MaximumLength(Customer.NotesMaxLength);
    }
}

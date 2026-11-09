using FluentValidation;

namespace OfiFlow.Application.Quotes.Commands.AcceptQuote;

public sealed class AcceptQuoteCommandValidator : AbstractValidator<AcceptQuoteCommand>
{
    public AcceptQuoteCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

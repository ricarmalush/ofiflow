using FluentValidation;

namespace OfiFlow.Application.Quotes.Commands.RejectQuote;

public sealed class RejectQuoteCommandValidator : AbstractValidator<RejectQuoteCommand>
{
    public RejectQuoteCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

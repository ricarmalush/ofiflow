using FluentValidation;

namespace OfiFlow.Application.Quotes.Commands.SendQuote;

public sealed class SendQuoteCommandValidator : AbstractValidator<SendQuoteCommand>
{
    public SendQuoteCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

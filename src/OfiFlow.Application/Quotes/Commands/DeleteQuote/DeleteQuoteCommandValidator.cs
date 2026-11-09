using FluentValidation;

namespace OfiFlow.Application.Quotes.Commands.DeleteQuote;

public sealed class DeleteQuoteCommandValidator : AbstractValidator<DeleteQuoteCommand>
{
    public DeleteQuoteCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

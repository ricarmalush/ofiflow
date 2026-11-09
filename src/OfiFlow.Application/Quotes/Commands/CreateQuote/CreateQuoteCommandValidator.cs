using FluentValidation;

namespace OfiFlow.Application.Quotes.Commands.CreateQuote;

public sealed class CreateQuoteCommandValidator : AbstractValidator<CreateQuoteCommand>
{
    public CreateQuoteCommandValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.JobId).NotEqual(Guid.Empty).When(x => x.JobId.HasValue);
        RuleFor(x => x.Title).ValidQuoteTitle();
        RuleFor(x => x.Notes).ValidQuoteNotes();
        RuleFor(x => x.ValidUntil!.Value).ValidQuoteValidUntil().OverridePropertyName(nameof(CreateQuoteCommand.ValidUntil)).When(x => x.ValidUntil.HasValue);
        RuleFor(x => x.Items).ValidQuoteItems();
    }
}

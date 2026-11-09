using FluentValidation;

namespace OfiFlow.Application.Quotes.Commands.UpdateQuote;

public sealed class UpdateQuoteCommandValidator : AbstractValidator<UpdateQuoteCommand>
{
    public UpdateQuoteCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Version).Must(QuoteVersion.IsWellFormed).WithMessage("La versión del presupuesto no es válida.");
        RuleFor(x => x.JobId).NotEqual(Guid.Empty).When(x => x.JobId.HasValue);
        RuleFor(x => x.Title).ValidQuoteTitle();
        RuleFor(x => x.Notes).ValidQuoteNotes();
        RuleFor(x => x.ValidUntil!.Value).ValidQuoteValidUntil().OverridePropertyName(nameof(UpdateQuoteCommand.ValidUntil)).When(x => x.ValidUntil.HasValue);
        RuleFor(x => x.Items).ValidQuoteItems();
    }
}

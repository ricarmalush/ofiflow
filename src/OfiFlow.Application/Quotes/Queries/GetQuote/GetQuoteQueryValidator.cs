using FluentValidation;

namespace OfiFlow.Application.Quotes.Queries.GetQuote;

public sealed class GetQuoteQueryValidator : AbstractValidator<GetQuoteQuery>
{
    public GetQuoteQueryValidator() => RuleFor(x => x.Id).NotEmpty();
}

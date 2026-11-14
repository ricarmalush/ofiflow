using FluentValidation;

namespace OfiFlow.Application.Quotes.Queries.GetQuotePdf;

public sealed class GetQuotePdfQueryValidator : AbstractValidator<GetQuotePdfQuery>
{
    public GetQuotePdfQueryValidator() => RuleFor(x => x.Id).NotEmpty();
}

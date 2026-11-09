using FluentValidation;

namespace OfiFlow.Application.Quotes.Queries.GetQuotes;

public sealed class GetQuotesQueryValidator : AbstractValidator<GetQuotesQuery>
{
    public GetQuotesQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, GetQuotesQuery.MaxPageSize);
        RuleFor(x => x.Status!.Value).IsInEnum().OverridePropertyName(nameof(GetQuotesQuery.Status)).When(x => x.Status.HasValue);
        RuleFor(x => x.CustomerId!.Value).NotEqual(Guid.Empty).OverridePropertyName(nameof(GetQuotesQuery.CustomerId)).When(x => x.CustomerId.HasValue);
    }
}

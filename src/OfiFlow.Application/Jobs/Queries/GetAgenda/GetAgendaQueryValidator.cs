using FluentValidation;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Queries.GetAgenda;

/// <summary>
/// El rango de la agenda (spec 020 R11): los dos extremos con desfase explícito, el final después del principio y no más de
/// <see cref="GetAgendaQuery.MaxRange"/>. Todo con el mismo código: la persona que ve el error solo necesita saber que el rango no vale.
/// </summary>
public sealed class GetAgendaQueryValidator : AbstractValidator<GetAgendaQuery>
{
    public GetAgendaQueryValidator()
    {
        RuleFor(x => x.From).Must(IsInstant).WithErrorCode(JobErrors.AgendaRangeInvalid);
        RuleFor(x => x.To).Must(IsInstant).WithErrorCode(JobErrors.AgendaRangeInvalid);

        // La relación entre los dos solo se mira si cada uno es un instante válido; el fallo se atribuye al final.
        RuleFor(x => x.To)
            .Must((query, _) => HasValidSpan(query))
            .WithErrorCode(JobErrors.AgendaRangeInvalid)
            .When(x => IsInstant(x.From) && IsInstant(x.To));
    }

    private static bool IsInstant(string? value) => IsoInstant.TryParse(value, out _);

    private static bool HasValidSpan(GetAgendaQuery query)
    {
        if (!IsoInstant.TryParse(query.From, out var from) || !IsoInstant.TryParse(query.To, out var to))
        {
            return false;
        }

        return to > from && to - from <= GetAgendaQuery.MaxRange;
    }
}

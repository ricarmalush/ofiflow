using FluentValidation;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.ScheduleJob;

public sealed class ScheduleJobCommandValidator : AbstractValidator<ScheduleJobCommand>
{
    public ScheduleJobCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();

        // Forma exacta con desfase y cotas de cordura (R4, R5). Una fecha pasada NO se rechaza (D5).
        RuleFor(x => x.ScheduledDate)
            .Must(IsWithinBounds)
            .WithErrorCode(JobErrors.ScheduleDateInvalid);

        RuleFor(x => x.EstimatedDurationMinutes!.Value)
            .InclusiveBetween(Job.EstimatedDurationMinutesMin, Job.EstimatedDurationMinutesMax)
            .WithErrorCode(JobErrors.EstimatedDurationInvalid)
            .OverridePropertyName(nameof(ScheduleJobCommand.EstimatedDurationMinutes))
            .When(x => x.EstimatedDurationMinutes.HasValue);
    }

    private static bool IsWithinBounds(string? value) =>
        IsoInstant.TryParse(value, out var utc) && utc >= Job.ScheduledDateMin && utc < Job.ScheduledDateMax;
}

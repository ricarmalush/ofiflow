using FluentValidation;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.CreateJob;

public sealed class CreateJobCommandValidator : AbstractValidator<CreateJobCommand>
{
    public CreateJobCommandValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Job.TitleMaxLength);
        RuleFor(x => x.Description).MaximumLength(Job.DescriptionMaxLength);
        RuleFor(x => x.Priority).IsInEnum();
    }
}

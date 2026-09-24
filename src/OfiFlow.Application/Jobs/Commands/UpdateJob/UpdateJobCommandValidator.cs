using FluentValidation;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.UpdateJob;

public sealed class UpdateJobCommandValidator : AbstractValidator<UpdateJobCommand>
{
    public UpdateJobCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Job.TitleMaxLength);
        RuleFor(x => x.Description).MaximumLength(Job.DescriptionMaxLength);
        RuleFor(x => x.Priority).IsInEnum();
    }
}

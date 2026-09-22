using FluentValidation;

namespace OfiFlow.Application.Jobs.Commands.AssignJob;

public sealed class AssignJobCommandValidator : AbstractValidator<AssignJobCommand>
{
    public AssignJobCommandValidator()
    {
        RuleFor(x => x.JobId).NotEmpty();
        RuleFor(x => x.TenantUserId).NotEmpty();
    }
}

using FluentValidation.TestHelper;
using OfiFlow.Application.Jobs.Commands.CreateJob;
using OfiFlow.Application.Jobs.Commands.UpdateJob;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>ADR-009 R3: cada campo de texto acepta su longitud máxima exacta y rechaza una más.</summary>
public class JobValidatorTests
{
    private readonly CreateJobCommandValidator _create = new();
    private readonly UpdateJobCommandValidator _update = new();

    private static CreateJobCommand ValidCreate() =>
        new(Guid.NewGuid(), "Fuga bajo el fregadero", "Descripción", JobPriority.Normal);

    [Fact]
    public void ValidCommand_HasNoErrors()
    {
        _create.TestValidate(ValidCreate()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Title_AtMaxLength_IsValid_AndOneMore_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Title = new string('a', Job.TitleMaxLength) })
            .ShouldNotHaveValidationErrorFor(x => x.Title);

        _create.TestValidate(ValidCreate() with { Title = new string('a', Job.TitleMaxLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Description_AtMaxLength_IsValid_AndOneMore_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Description = new string('a', Job.DescriptionMaxLength) })
            .ShouldNotHaveValidationErrorFor(x => x.Description);

        _create.TestValidate(ValidCreate() with { Description = new string('a', Job.DescriptionMaxLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Priority_OutOfEnumRange_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Priority = (JobPriority)999 })
            .ShouldHaveValidationErrorFor(x => x.Priority);
    }

    [Fact]
    public void Update_AppliesTheSameLimits()
    {
        var command = new UpdateJobCommand(
            Guid.NewGuid(),
            new string('a', Job.TitleMaxLength + 1),
            new string('a', Job.DescriptionMaxLength + 1),
            (JobPriority)999);

        var result = _update.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Title);
        result.ShouldHaveValidationErrorFor(x => x.Description);
        result.ShouldHaveValidationErrorFor(x => x.Priority);
    }
}

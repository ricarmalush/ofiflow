using FluentValidation.TestHelper;
using OfiFlow.Application.Jobs.Commands.ScheduleJob;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>Las entradas de la programación (spec 020 R4 a R6): forma, cotas de la fecha y de la duración, y qué código sale de cada fallo.</summary>
public class ScheduleJobValidatorTests
{
    private readonly ScheduleJobCommandValidator _validator = new();

    private static ScheduleJobCommand Valid() => new(Guid.NewGuid(), "2026-11-10T09:00:00+01:00", 90);

    [Fact]
    public void ACompleteCommand_HasNoErrors() =>
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void WithoutDuration_IsValid() =>
        _validator.TestValidate(Valid() with { EstimatedDurationMinutes = null }).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AnEmptyId_IsInvalid() =>
        _validator.TestValidate(Valid() with { Id = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.Id);

    [Theory]
    [InlineData("2026-11-10T09:00:00")]
    [InlineData("2026-11-10")]
    [InlineData("mañana")]
    [InlineData("")]
    public void ADateWithoutOffsetOrNotADate_IsInvalid_WithItsCode(string date) =>
        _validator.TestValidate(Valid() with { ScheduledDate = date })
            .ShouldHaveValidationErrorFor(x => x.ScheduledDate)
            .WithErrorCode(JobErrors.ScheduleDateInvalid);

    [Fact]
    public void ANullDate_IsInvalid() =>
        _validator.TestValidate(Valid() with { ScheduledDate = null! })
            .ShouldHaveValidationErrorFor(x => x.ScheduledDate)
            .WithErrorCode(JobErrors.ScheduleDateInvalid);

    [Theory]
    [InlineData("2000-01-01T00:00:00Z")]       // el límite inferior está dentro
    [InlineData("2099-12-31T23:59:59Z")]       // el último segundo de 2099
    [InlineData("2100-01-01T00:00:00+01:00")]  // son las 23:00 UTC del 31/12/2099: dentro
    public void TheBoundsAreTheUtcInstant_InsideCases(string date) =>
        _validator.TestValidate(Valid() with { ScheduledDate = date }).ShouldNotHaveValidationErrorFor(x => x.ScheduledDate);

    [Theory]
    [InlineData("1999-12-31T23:59:59Z")]       // un segundo antes del límite
    [InlineData("2100-01-01T00:00:00Z")]       // el límite superior está fuera
    [InlineData("2000-01-01T00:00:00+01:00")]  // son las 23:00 UTC del 31/12/1999: fuera
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    public void TheBoundsAreTheUtcInstant_OutsideCases(string date) =>
        _validator.TestValidate(Valid() with { ScheduledDate = date })
            .ShouldHaveValidationErrorFor(x => x.ScheduledDate)
            .WithErrorCode(JobErrors.ScheduleDateInvalid);

    [Fact]
    public void APastDate_IsNotRejected_SpecDecisionD5() =>
        _validator.TestValidate(Valid() with { ScheduledDate = "2020-03-01T10:00:00Z" })
            .ShouldNotHaveValidationErrorFor(x => x.ScheduledDate);

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public void TheDurationLimitsThemselves_AreValid(int minutes) =>
        _validator.TestValidate(Valid() with { EstimatedDurationMinutes = minutes })
            .ShouldNotHaveValidationErrorFor(x => x.EstimatedDurationMinutes);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ADurationOutsideTheLimits_IsInvalid_WithItsCode(int minutes) =>
        _validator.TestValidate(Valid() with { EstimatedDurationMinutes = minutes })
            .ShouldHaveValidationErrorFor(x => x.EstimatedDurationMinutes)
            .WithErrorCode(JobErrors.EstimatedDurationInvalid);
}

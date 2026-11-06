using FluentValidation.TestHelper;
using OfiFlow.Application.Jobs.Queries.GetAgenda;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>El rango de la agenda (spec 020 R11): los dos extremos con desfase, el final después del principio y no más de 42 días.</summary>
public class GetAgendaValidatorTests
{
    private readonly GetAgendaQueryValidator _validator = new();

    private static GetAgendaQuery Week() => new("2026-11-09T00:00:00Z", "2026-11-16T00:00:00Z");

    [Fact]
    public void AOneWeekRange_IsValid() =>
        _validator.TestValidate(Week()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void TheEndpointsCanCarryDifferentOffsets_TheyAreComparedAsInstants() =>
        // 00:00 en Madrid del lunes a 00:00 en Madrid del lunes siguiente (UTC+1 en noviembre).
        _validator.TestValidate(new GetAgendaQuery("2026-11-09T00:00:00+01:00", "2026-11-16T00:00:00+01:00")).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ARangeOfExactlyTheMaximum_IsValid() =>
        _validator.TestValidate(new GetAgendaQuery("2026-11-01T00:00:00Z", "2026-12-13T00:00:00Z")).ShouldNotHaveAnyValidationErrors(); // 42 días justos

    [Fact]
    public void ARangeOneSecondOverTheMaximum_IsInvalid() =>
        _validator.TestValidate(new GetAgendaQuery("2026-11-01T00:00:00Z", "2026-12-13T00:00:01Z"))
            .ShouldHaveValidationErrorFor(x => x.To)
            .WithErrorCode(JobErrors.AgendaRangeInvalid);

    [Fact]
    public void TheMaximum_IsSixWeeks() =>
        Assert.Equal(TimeSpan.FromDays(42), GetAgendaQuery.MaxRange);

    [Fact]
    public void AnEndEqualToTheStart_IsInvalid() =>
        _validator.TestValidate(new GetAgendaQuery("2026-11-09T00:00:00Z", "2026-11-09T00:00:00Z"))
            .ShouldHaveValidationErrorFor(x => x.To)
            .WithErrorCode(JobErrors.AgendaRangeInvalid);

    [Fact]
    public void AnEndBeforeTheStart_IsInvalid() =>
        _validator.TestValidate(new GetAgendaQuery("2026-11-16T00:00:00Z", "2026-11-09T00:00:00Z"))
            .ShouldHaveValidationErrorFor(x => x.To)
            .WithErrorCode(JobErrors.AgendaRangeInvalid);

    [Fact]
    public void TheSameInstantInTwoOffsets_IsAnEmptyRange_NotAValidOne() =>
        _validator.TestValidate(new GetAgendaQuery("2026-11-09T09:00:00+01:00", "2026-11-09T08:00:00Z"))
            .ShouldHaveValidationErrorFor(x => x.To);

    [Theory]
    [InlineData("2026-11-09T00:00:00")]
    [InlineData("2026-11-09")]
    [InlineData("lunes")]
    [InlineData("")]
    public void AStartWithoutOffsetOrNotADate_IsInvalid_WithItsCode(string from) =>
        _validator.TestValidate(Week() with { From = from })
            .ShouldHaveValidationErrorFor(x => x.From)
            .WithErrorCode(JobErrors.AgendaRangeInvalid);

    [Theory]
    [InlineData("2026-11-16T00:00:00")]
    [InlineData("2026-11-16")]
    [InlineData("domingo")]
    [InlineData("")]
    public void AnEndWithoutOffsetOrNotADate_IsInvalid_WithItsCode(string to) =>
        _validator.TestValidate(Week() with { To = to })
            .ShouldHaveValidationErrorFor(x => x.To)
            .WithErrorCode(JobErrors.AgendaRangeInvalid);

    [Fact]
    public void AMissingStart_IsInvalid_NotAnException() =>
        _validator.TestValidate(Week() with { From = null! }).ShouldHaveValidationErrorFor(x => x.From);

    [Fact]
    public void AMissingEnd_IsInvalid_NotAnException() =>
        _validator.TestValidate(Week() with { To = null! }).ShouldHaveValidationErrorFor(x => x.To);

    [Fact]
    public void WhenAnEndpointIsNotAnInstant_TheRelationBetweenThemIsNotJudged()
    {
        // Un solo error por extremo roto: la relación (orden y tope) solo se evalúa si los dos son instantes.
        var result = _validator.TestValidate(new GetAgendaQuery("no-es-una-fecha", "2026-11-16T00:00:00Z"));

        result.ShouldHaveValidationErrorFor(x => x.From);
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == nameof(GetAgendaQuery.To));
    }

    [Fact]
    public void ARangeFarOutsideTheJobDates_IsStillJustARange_TheBoundsDoNotApplyToTheQuery() =>
        // La agenda es de consulta: pedir años lejanos no es un error de seguridad, solo devolverá vacío.
        _validator.TestValidate(new GetAgendaQuery("1900-01-01T00:00:00Z", "1900-01-08T00:00:00Z")).ShouldNotHaveAnyValidationErrors();
}

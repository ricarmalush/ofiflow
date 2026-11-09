using FluentValidation.TestHelper;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Commands.AcceptQuote;
using OfiFlow.Application.Quotes.Commands.CreateQuote;
using OfiFlow.Application.Quotes.Commands.DeleteQuote;
using OfiFlow.Application.Quotes.Commands.RejectQuote;
using OfiFlow.Application.Quotes.Commands.SendQuote;
using OfiFlow.Application.Quotes.Commands.UpdateQuote;
using OfiFlow.Application.Quotes.Queries.GetQuote;
using OfiFlow.Application.Quotes.Queries.GetQuotes;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>
/// La forma de lo que llega al crear y modificar un presupuesto (spec 023 R2 y R3): cada cota, <b>en sus dos lados</b>, y qué código sale de cada fallo.
/// Los validadores comprueban la forma antes de gastar nada; el dominio vuelve a vigilar las mismas cotas por si alguien llama sin pasar por ellos.
/// </summary>
public class QuoteValidatorTests
{
    private readonly CreateQuoteCommandValidator _create = new();
    private readonly UpdateQuoteCommandValidator _update = new();

    private static QuoteItemInput Line(decimal quantity = 1, long price = 10000, int vat = 21, string description = "Mano de obra") => new(description, quantity, price, vat);

    private static CreateQuoteCommand ValidCreate() =>
        new(Guid.NewGuid(), null, "Reforma del baño", "Condiciones", new DateOnly(2026, 12, 31), [Line()]);

    private static UpdateQuoteCommand ValidUpdate() =>
        new(Guid.NewGuid(), "AQIDBA==", null, "Reforma del baño", "Condiciones", new DateOnly(2026, 12, 31), [Line()]);

    [Fact]
    public void ACompleteCreate_HasNoErrors() => _create.TestValidate(ValidCreate()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ACompleteUpdate_HasNoErrors() => _update.TestValidate(ValidUpdate()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ADraftWithoutLinesAndWithoutOptionalData_IsValid()
    {
        _create.TestValidate(ValidCreate() with { Items = [], Notes = null, ValidUntil = null }).ShouldNotHaveAnyValidationErrors();
        _update.TestValidate(ValidUpdate() with { Items = [], Notes = null, ValidUntil = null }).ShouldNotHaveAnyValidationErrors();
    }

    // ---------- Cabecera ----------

    [Fact]
    public void AnEmptyCustomer_IsInvalid() => _create.TestValidate(ValidCreate() with { CustomerId = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.CustomerId);

    [Fact]
    public void AnEmptyJobId_IsInvalid_ButNoJobIsValid()
    {
        _create.TestValidate(ValidCreate() with { JobId = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.JobId);
        _create.TestValidate(ValidCreate() with { JobId = null }).ShouldNotHaveValidationErrorFor(x => x.JobId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ATitle_ThatIsEmpty_IsInvalid(string title)
    {
        _create.TestValidate(ValidCreate() with { Title = title }).ShouldHaveValidationErrorFor(x => x.Title);
        _update.TestValidate(ValidUpdate() with { Title = title }).ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void TheTitle_AcceptsTheMaximum_AndNoMore()
    {
        _create.TestValidate(ValidCreate() with { Title = new string('t', Quote.TitleMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.Title);
        _create.TestValidate(ValidCreate() with { Title = new string('t', Quote.TitleMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.Title);
        _update.TestValidate(ValidUpdate() with { Title = new string('t', Quote.TitleMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void TheNotes_AcceptTheMaximum_AndNoMore()
    {
        _create.TestValidate(ValidCreate() with { Notes = new string('n', Quote.NotesMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.Notes);
        _create.TestValidate(ValidCreate() with { Notes = new string('n', Quote.NotesMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.Notes);
        _update.TestValidate(ValidUpdate() with { Notes = new string('n', Quote.NotesMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.Notes);
    }

    [Fact]
    public void TheValidUntil_AcceptsTheEndsOfTheRange_AndGivesItsOwnCodeOutside()
    {
        _create.TestValidate(ValidCreate() with { ValidUntil = new DateOnly(2000, 1, 1) }).ShouldNotHaveValidationErrorFor(x => x.ValidUntil);
        _create.TestValidate(ValidCreate() with { ValidUntil = new DateOnly(2099, 12, 31) }).ShouldNotHaveValidationErrorFor(x => x.ValidUntil);

        foreach (var outside in new[] { new DateOnly(1999, 12, 31), new DateOnly(2100, 1, 1) })
        {
            _create.TestValidate(ValidCreate() with { ValidUntil = outside }).ShouldHaveValidationErrorFor(x => x.ValidUntil).WithErrorCode(QuoteErrors.ValidUntilInvalid);
            _update.TestValidate(ValidUpdate() with { ValidUntil = outside }).ShouldHaveValidationErrorFor(x => x.ValidUntil).WithErrorCode(QuoteErrors.ValidUntilInvalid);
        }
    }

    [Fact]
    public void APastValidUntil_IsValid() =>
        _create.TestValidate(ValidCreate() with { ValidUntil = new DateOnly(2001, 5, 5) }).ShouldNotHaveValidationErrorFor(x => x.ValidUntil);

    // ---------- Modificar ----------

    [Fact]
    public void AnEmptyId_IsInvalid() => _update.TestValidate(ValidUpdate() with { Id = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.Id);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto no es base64!")]
    [InlineData("AQID=")]
    public void AMissingOrMalformedVersion_IsInvalid(string version) =>
        _update.TestValidate(ValidUpdate() with { Version = version }).ShouldHaveValidationErrorFor(x => x.Version);

    [Fact]
    public void AWellFormedVersion_IsValid()
    {
        Assert.True(QuoteVersion.IsWellFormed(Convert.ToBase64String([0, 0, 0, 0, 0, 0, 7, 209])));
        Assert.False(QuoteVersion.IsWellFormed(null));
    }

    // ---------- Líneas ----------

    [Fact]
    public void WithoutAListOfLines_IsInvalid() =>
        _create.TestValidate(ValidCreate() with { Items = null! }).ShouldHaveValidationErrorFor(x => x.Items);

    [Fact]
    public void TheLines_AcceptTheMaximumCount_AndNoMore()
    {
        _create.TestValidate(ValidCreate() with { Items = Enumerable.Repeat(Line(), Quote.MaxItems).ToList() }).ShouldNotHaveValidationErrorFor(x => x.Items);
        _create.TestValidate(ValidCreate() with { Items = Enumerable.Repeat(Line(), Quote.MaxItems + 1).ToList() }).ShouldHaveValidationErrorFor(x => x.Items);
        _update.TestValidate(ValidUpdate() with { Items = Enumerable.Repeat(Line(), Quote.MaxItems + 1).ToList() }).ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void ANullLine_IsInvalid_NotAnException() =>
        Assert.False(_create.TestValidate(ValidCreate() with { Items = [Line(), null!] }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ALine_WithoutDescription_IsInvalid(string description) =>
        Assert.False(_create.TestValidate(ValidCreate() with { Items = [Line(description: description)] }).IsValid);

    [Fact]
    public void TheDescription_AcceptsTheMaximum_AndNoMore()
    {
        _create.TestValidate(ValidCreate() with { Items = [Line(description: new string('d', Quote.DescriptionMaxLength))] }).ShouldNotHaveAnyValidationErrors();
        Assert.False(_create.TestValidate(ValidCreate() with { Items = [Line(description: new string('d', Quote.DescriptionMaxLength + 1))] }).IsValid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0004")]
    [InlineData("1.0001")]
    [InlineData("100000")]
    [InlineData("99999.9991")]
    public void AnInvalidQuantity_GivesItsOwnCode(string quantity)
    {
        var result = _create.TestValidate(ValidCreate() with { Items = [Line(quantity: decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture))] });

        Assert.Contains(result.Errors, e => e.ErrorCode == QuoteErrors.QuantityInvalid);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("1")]
    [InlineData("2.5")]
    [InlineData("99999.999")]
    public void AValidQuantity_IsAccepted(string quantity) =>
        _create.TestValidate(ValidCreate() with { Items = [Line(quantity: decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture))] }).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ThePrice_AcceptsZeroAndTheMaximum_ButNotNegativeNorOver()
    {
        _create.TestValidate(ValidCreate() with { Items = [Line(price: 0)] }).ShouldNotHaveAnyValidationErrors();
        _create.TestValidate(ValidCreate() with { Items = [Line(price: Quote.UnitPriceCentsMax)] }).ShouldNotHaveAnyValidationErrors();
        Assert.False(_create.TestValidate(ValidCreate() with { Items = [Line(price: -1)] }).IsValid);
        Assert.False(_create.TestValidate(ValidCreate() with { Items = [Line(price: Quote.UnitPriceCentsMax + 1)] }).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(21)]
    public void EveryAllowedVatRate_IsAccepted(int vat) =>
        _create.TestValidate(ValidCreate() with { Items = [Line(vat: vat)] }).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(22)]
    [InlineData(100)]
    public void AnyOtherVatRate_GivesItsOwnCode(int vat)
    {
        var result = _create.TestValidate(ValidCreate() with { Items = [Line(vat: vat)] });

        Assert.Contains(result.Errors, e => e.ErrorCode == QuoteErrors.VatInvalid);
    }

    [Fact]
    public void TheErrorOfALine_PointsAtThatLine()
    {
        var result = _create.TestValidate(ValidCreate() with { Items = [Line(), Line(vat: 13)] });

        Assert.Contains(result.Errors, e => e.PropertyName.Contains("[1]", StringComparison.Ordinal) && e.ErrorCode == QuoteErrors.VatInvalid);
        Assert.DoesNotContain(result.Errors, e => e.PropertyName.Contains("[0]", StringComparison.Ordinal));
    }

    // ---------- Operaciones por Id y consultas ----------

    [Fact]
    public void TheOperationsOnOneQuote_NeedAnId()
    {
        Assert.False(new DeleteQuoteCommandValidator().TestValidate(new DeleteQuoteCommand(Guid.Empty)).IsValid);
        Assert.False(new SendQuoteCommandValidator().TestValidate(new SendQuoteCommand(Guid.Empty)).IsValid);
        Assert.False(new AcceptQuoteCommandValidator().TestValidate(new AcceptQuoteCommand(Guid.Empty)).IsValid);
        Assert.False(new RejectQuoteCommandValidator().TestValidate(new RejectQuoteCommand(Guid.Empty)).IsValid);
        Assert.False(new GetQuoteQueryValidator().TestValidate(new GetQuoteQuery(Guid.Empty)).IsValid);

        Assert.True(new DeleteQuoteCommandValidator().TestValidate(new DeleteQuoteCommand(Guid.NewGuid())).IsValid);
        Assert.True(new SendQuoteCommandValidator().TestValidate(new SendQuoteCommand(Guid.NewGuid())).IsValid);
        Assert.True(new AcceptQuoteCommandValidator().TestValidate(new AcceptQuoteCommand(Guid.NewGuid())).IsValid);
        Assert.True(new RejectQuoteCommandValidator().TestValidate(new RejectQuoteCommand(Guid.NewGuid())).IsValid);
        Assert.True(new GetQuoteQueryValidator().TestValidate(new GetQuoteQuery(Guid.NewGuid())).IsValid);
    }

    [Fact]
    public void TheListing_AcceptsPagesOfOneToOneHundred_AndNothingElse()
    {
        var validator = new GetQuotesQueryValidator();

        Assert.True(validator.TestValidate(new GetQuotesQuery(null, null, 1, 1)).IsValid);
        Assert.True(validator.TestValidate(new GetQuotesQuery(null, null, 1, 100)).IsValid);
        Assert.True(validator.TestValidate(new GetQuotesQuery(null, null)).IsValid);
        Assert.False(validator.TestValidate(new GetQuotesQuery(null, null, 1, 0)).IsValid);
        Assert.False(validator.TestValidate(new GetQuotesQuery(null, null, 1, 101)).IsValid);
        Assert.False(validator.TestValidate(new GetQuotesQuery(null, null, 0, 25)).IsValid);
        Assert.False(validator.TestValidate(new GetQuotesQuery(null, null, -3, 25)).IsValid);
    }

    [Fact]
    public void TheListingFilters_MustBeValuesThatExist()
    {
        var validator = new GetQuotesQueryValidator();

        Assert.True(validator.TestValidate(new GetQuotesQuery(QuoteStatus.Sent, Guid.NewGuid())).IsValid);
        Assert.False(validator.TestValidate(new GetQuotesQuery((QuoteStatus)99, null)).IsValid);
        Assert.False(validator.TestValidate(new GetQuotesQuery(null, Guid.Empty)).IsValid);
    }
}

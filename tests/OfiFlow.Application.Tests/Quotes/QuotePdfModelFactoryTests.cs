using OfiFlow.Application.Company;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Pdf;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// Lo que se imprime en el PDF de un presupuesto (spec 025 R4, R5, D12): sale del <c>QuoteDto</c> que ya calculó el servidor y del perfil fiscal, <b>sin recalcular nada</b>, y no lleva más datos del
/// cliente que su nombre y su dirección.
/// </summary>
public class QuotePdfModelFactoryTests
{
    private static CompanyProfileDto Company(
        string? province = "Madrid",
        string? phone = "912345678",
        string? email = "marta@fontaneria-gil.example") =>
        new("Fontanería Marta Gil S.L.", "B84531946", "Calle de Alcalá 123, 2º B", "28009", "Madrid", province, phone, email, IsComplete: true);

    private static QuoteDto Quote(
        string status = "Sent",
        int number = 12,
        IReadOnlyList<QuoteItemDto>? items = null,
        IReadOnlyList<VatBreakdownDto>? breakdown = null,
        long net = 10_000,
        long vat = 2_100,
        long total = 12_100,
        string? notes = "Pago a 30 días.",
        DateOnly? validUntil = null,
        DateTime? createdAt = null) =>
        new(
            Guid.NewGuid(),
            number,
            Guid.NewGuid(),
            "Construcciones Pérez e Hijos",
            JobId: null,
            "Sustitución de caldera",
            notes,
            validUntil,
            status,
            net,
            vat,
            total,
            breakdown ?? [new VatBreakdownDto(21, 10_000, 2_100)],
            items ?? [new QuoteItemDto(Guid.NewGuid(), 0, "Caldera", 1m, 10_000, 21, 10_000)],
            SentAt: null,
            DecidedAt: null,
            createdAt ?? new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
            "AAAA");

    [Fact]
    public void ItCopiesTheAmountsOfTheDtoAndNeverRecalculatesThem()
    {
        // Un DTO deliberadamente incoherente: si el generador sumara, multiplicara o redondeara, los importes impresos serían otros.
        var quote = Quote(
            items: [new QuoteItemDto(Guid.NewGuid(), 0, "Una", 3m, 1_000, 21, 777), new QuoteItemDto(Guid.NewGuid(), 1, "Otra", 2m, 500, 10, 555)],
            breakdown: [new VatBreakdownDto(10, 123, 45), new VatBreakdownDto(21, 678, 90)],
            net: 111,
            vat: 222,
            total: 400);

        var model = QuotePdfModelFactory.Create(quote, Company(), customerAddress: null);

        Assert.Equal(111, model.NetCents);
        Assert.Equal(222, model.VatCents);
        Assert.Equal(400, model.TotalCents);
        Assert.Equal([777L, 555L], model.Lines.Select(l => l.NetCents));
        Assert.Equal([(10, 123L, 45L), (21, 678L, 90L)], model.Breakdown.Select(b => (b.VatPercent, b.BaseCents, b.VatCents)));
        Assert.Equal([3m, 2m], model.Lines.Select(l => l.Quantity));
        Assert.Equal([1_000L, 500L], model.Lines.Select(l => l.UnitPriceCents));
        Assert.Equal([21, 10], model.Lines.Select(l => l.VatPercent));
    }

    [Fact]
    public void TheLinesComeInTheirPositionOrderWhateverOrderTheDtoHasThem()
    {
        var quote = Quote(items:
        [
            new QuoteItemDto(Guid.NewGuid(), 2, "Tercera", 1m, 1, 21, 1),
            new QuoteItemDto(Guid.NewGuid(), 0, "Primera", 1m, 1, 21, 1),
            new QuoteItemDto(Guid.NewGuid(), 1, "Segunda", 1m, 1, 21, 1)
        ]);

        var model = QuotePdfModelFactory.Create(quote, Company(), null);

        Assert.Equal(["Primera", "Segunda", "Tercera"], model.Lines.Select(l => l.Description));
    }

    [Fact]
    public void TheIssuerIsTheFiscalProfileOfTheCompany()
    {
        var model = QuotePdfModelFactory.Create(Quote(), Company(), null);

        Assert.Equal("Fontanería Marta Gil S.L.", model.Issuer.LegalName);
        Assert.Equal("B84531946", model.Issuer.TaxId);
        Assert.Equal("Calle de Alcalá 123, 2º B", model.Issuer.AddressLine);
        Assert.Equal("28009", model.Issuer.PostalCode);
        Assert.Equal("Madrid", model.Issuer.City);
        Assert.Equal("Madrid", model.Issuer.Province);
        Assert.Equal("912345678", model.Issuer.Phone);
        Assert.Equal("marta@fontaneria-gil.example", model.Issuer.Email);
    }

    [Fact]
    public void TheOptionalDataOfTheIssuerStayNullWhenTheCompanyHasNone()
    {
        var model = QuotePdfModelFactory.Create(Quote(), Company(province: null, phone: null, email: null), null);

        Assert.Null(model.Issuer.Province);
        Assert.Null(model.Issuer.Phone);
        Assert.Null(model.Issuer.Email);
    }

    [Fact]
    public void TheDocumentCarriesTheLabelTitleValidityAndNotes()
    {
        var model = QuotePdfModelFactory.Create(Quote(number: 12, validUntil: new DateOnly(2026, 12, 31)), Company(), null);

        Assert.Equal("P-0012", model.Label);
        Assert.Equal("Sustitución de caldera", model.Title);
        Assert.Equal(new DateOnly(2026, 12, 31), model.ValidUntil);
        Assert.Equal("Pago a 30 días.", model.Notes);
    }

    [Fact]
    public void WithoutValidityOrNotesTheModelHasNeither()
    {
        var model = QuotePdfModelFactory.Create(Quote(notes: null, validUntil: null), Company(), null);

        Assert.Null(model.ValidUntil);
        Assert.Null(model.Notes);
    }

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Sent", false)]
    [InlineData("Accepted", false)]
    [InlineData("Rejected", false)]
    public void OnlyADraftIsMarkedAsDraft(string status, bool expected)
    {
        var model = QuotePdfModelFactory.Create(Quote(status: status), Company(), null);

        Assert.Equal(expected, model.IsDraft);
    }

    [Fact]
    public void TheCustomerIsOnlyItsNameAndItsAddress()
    {
        var model = QuotePdfModelFactory.Create(Quote(), Company(), "Avenida de América 45, 28028 Madrid");

        Assert.Equal("Construcciones Pérez e Hijos", model.Customer.Name);
        Assert.Equal("Avenida de América 45, 28028 Madrid", model.Customer.Address);

        // Por construcción: el cliente del documento no tiene dónde guardar notas, email ni teléfono (spec 025 R4).
        Assert.Equal(["Address", "Name"], typeof(QuotePdfCustomer).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCustomerAddressIsNoAddress(string? address)
    {
        var model = QuotePdfModelFactory.Create(Quote(), Company(), address);

        Assert.Null(model.Customer.Address);
    }

    [Fact]
    public void AnIncompleteProfileIsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => QuotePdfModelFactory.Create(Quote(), CompanyProfileDto.Empty, null));
    }

    [Theory]
    [InlineData("LegalName")]
    [InlineData("TaxId")]
    [InlineData("AddressLine")]
    [InlineData("PostalCode")]
    [InlineData("City")]
    public void EachMissingRequiredFieldIsAProgrammingErrorEvenIfTheProfileSaysItIsComplete(string missing)
    {
        var profile = Company() with
        {
            LegalName = missing == "LegalName" ? null : "Nombre",
            TaxId = missing == "TaxId" ? null : "B84531946",
            AddressLine = missing == "AddressLine" ? null : "Calle 1",
            PostalCode = missing == "PostalCode" ? null : "28009",
            City = missing == "City" ? null : "Madrid"
        };

        Assert.Throws<ArgumentException>(() => QuotePdfModelFactory.Create(Quote(), profile, null));
    }

    [Fact]
    public void AProfileThatSaysItIsNotCompleteIsRejectedEvenIfItHasEveryField()
    {
        // «Completo» es lo que decide el caso de uso (spec 024 R5): el generador no lo contradice aunque los campos estén.
        var profile = Company() with { IsComplete = false };

        Assert.Throws<ArgumentException>(() => QuotePdfModelFactory.Create(Quote(), profile, null));
    }

    [Fact]
    public void ANullArgumentIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => QuotePdfModelFactory.Create(null!, Company(), null));
        Assert.Throws<ArgumentNullException>(() => QuotePdfModelFactory.Create(Quote(), null!, null));
    }

    // Spec 025 D12: la fecha es la del día en Madrid, no en UTC. Cada caso es un instante en UTC y el día que se ve en Madrid.
    [Theory]
    [InlineData("2026-11-10T12:00:00", "2026-11-10")] // mediodía: el mismo día
    [InlineData("2026-12-31T23:30:00", "2027-01-01")] // invierno (UTC+1): 00:30 del día siguiente
    [InlineData("2026-12-31T22:59:59", "2026-12-31")] // invierno: un segundo antes de la medianoche de Madrid
    [InlineData("2026-12-31T23:00:00", "2027-01-01")] // invierno: justo la medianoche de Madrid
    [InlineData("2026-06-30T21:59:59", "2026-06-30")] // verano (UTC+2): un segundo antes
    [InlineData("2026-06-30T22:00:00", "2026-07-01")] // verano: justo la medianoche de Madrid
    [InlineData("2026-03-28T22:59:59", "2026-03-28")] // la noche antes del cambio a horario de verano (UTC+1)
    [InlineData("2026-03-28T23:00:00", "2026-03-29")] // medianoche de Madrid del día del cambio
    [InlineData("2026-03-29T00:59:59", "2026-03-29")] // el cambio es a la 01:00 UTC
    [InlineData("2026-03-29T01:00:00", "2026-03-29")] // ya en horario de verano (03:00)
    [InlineData("2026-03-29T21:59:59", "2026-03-29")] // el día del cambio dura 23 horas: aún es el 29
    [InlineData("2026-03-29T22:00:00", "2026-03-30")] // y ya es el 30 (UTC+2)
    [InlineData("2026-10-24T21:59:59", "2026-10-24")] // la noche antes del cambio a horario de invierno (UTC+2)
    [InlineData("2026-10-24T22:00:00", "2026-10-25")] // medianoche de Madrid del día del cambio
    [InlineData("2026-10-25T22:59:59", "2026-10-25")] // el día del cambio dura 25 horas: aún es el 25
    [InlineData("2026-10-25T23:00:00", "2026-10-26")] // y ya es el 26 (UTC+1)
    public void TheIssueDateIsTheDayInMadridNotInUtc(string utc, string expectedDay)
    {
        var instant = DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

        Assert.Equal(DateOnly.Parse(expectedDay, System.Globalization.CultureInfo.InvariantCulture), QuotePdfModelFactory.IssueDateOf(instant));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void AnInstantIsAlwaysTreatedAsUtcWhateverItsKind(DateTimeKind kind)
    {
        // La base de datos devuelve los instantes con Kind sin especificar; el huso del servidor no puede mover el día.
        var instant = DateTime.SpecifyKind(new DateTime(2026, 12, 31, 23, 30, 0), kind);

        Assert.Equal(new DateOnly(2027, 1, 1), QuotePdfModelFactory.IssueDateOf(instant));
    }

    [Fact]
    public void TheMadridTimeZoneExistsOnThisMachine()
    {
        // Si falla, el servidor no tiene la base de datos de zonas horarias (en una imagen mínima de Linux hay que instalarla): sin ella no se puede generar ningún PDF.
        Assert.NotNull(TimeZoneInfo.FindSystemTimeZoneById(QuotePdfModelFactory.TimeZoneId));
    }

    [Fact]
    public void TheDocumentTakesTheIssueDateFromTheCreationOfTheQuote()
    {
        var model = QuotePdfModelFactory.Create(Quote(createdAt: new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc)), Company(), null);

        Assert.Equal(new DateOnly(2027, 1, 1), model.IssueDate);
    }
}

using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Application.Quotes.Queries.GetQuotePdf;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// El caso de uso del PDF de un presupuesto (spec 025 R1, R3, R4, R5, R12), con dobles: el orden de las comprobaciones, que el modelo sale de los datos guardados y de nada más, que el cliente
/// no aporta más que su nombre y su dirección y que la consulta no escribe ni sigue entidades. El filtro por empresa real se prueba contra SQL Server en el bloque de extremo a extremo.
/// </summary>
public class GetQuotePdfQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Un generador de mentira: guarda el modelo que recibe y devuelve unos bytes reconocibles.</summary>
    private sealed class CapturingRenderer : IQuotePdfRenderer
    {
        public List<QuotePdfModel> Received { get; } = [];

        public byte[] Render(QuotePdfModel model)
        {
            Received.Add(model);
            return [0x25, 0x50, 0x44, 0x46];
        }
    }

    private static async Task<(TestDbContext Db, Customer Customer, Quote Quote)> SeedAsync(
        string customerName = "Ana Pérez",
        string? customerAddress = "Calle Luna 3, 28001 Madrid",
        string? customerNotes = "NOTA-INTERNA-del-cliente",
        bool withProfile = true,
        QuoteStatus status = QuoteStatus.Sent,
        int number = 12)
    {
        var db = TestDbContextFactory.Create();
        var customer = Customer.Create(TenantId, CustomerType.Person, customerName, Email.Create("secreto@example.com"), PhoneNumber.Create("600123456"), customerAddress, customerNotes);
        db.Customers.Add(customer);

        var quote = Quote.Create(
            TenantId, customer.Id, null, number, "Reforma del baño", "Pago a 30 días", new DateOnly(2026, 12, 31),
            [new QuoteItemInput("Mano de obra", 2.5m, 1234, 21), new QuoteItemInput("Material", 1m, 5000, 0)]);
        if (status != QuoteStatus.Draft)
        {
            quote.Send(Now);
        }

        db.Quotes.Add(quote);

        if (withProfile)
        {
            db.CompanyProfiles.Add(CompanyProfile.Create(
                TenantId, "Fontanería Marta Gil S.L.", SpanishTaxId.Create("B84531946"), "Calle de Alcalá 123", SpanishPostalCode.Create("28009"), "Madrid", "Madrid", PhoneNumber.Create("912345678"), Email.Create("marta@fontaneria-gil.example")));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        return (db, customer, quote);
    }

    private static Task<QuotePdfFile> RunAsync(TestDbContext db, CapturingRenderer renderer, Guid id) =>
        new GetQuotePdfQueryHandler(db, renderer).Handle(new GetQuotePdfQuery(id), CancellationToken.None);

    [Fact]
    public async Task ItReturnsTheRenderedBytesWithTheFileNameBuiltFromTheNumberAlone()
    {
        var (db, _, quote) = await SeedAsync(number: 12);
        var renderer = new CapturingRenderer();

        var file = await RunAsync(db, renderer, quote.Id);

        Assert.Equal("presupuesto-P-0012.pdf", file.FileName);
        Assert.Equal([0x25, 0x50, 0x44, 0x46], file.Content);
        Assert.Single(renderer.Received);
    }

    [Fact]
    public async Task TheFileNameNeverContainsFreeText()
    {
        var (db, _, quote) = await SeedAsync(customerName: "Ana \"Pérez\"\r\n../../etc/passwd", number: 7);

        var file = await RunAsync(db, new CapturingRenderer(), quote.Id);

        Assert.Equal("presupuesto-P-0007.pdf", file.FileName);
        Assert.DoesNotContain("Reforma", file.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana", file.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelComesFromTheStoredQuoteTheCustomerAndTheFiscalProfile()
    {
        var (db, _, quote) = await SeedAsync();
        var renderer = new CapturingRenderer();

        await RunAsync(db, renderer, quote.Id);
        var model = renderer.Received.Single();

        Assert.Equal("P-0012", model.Label);
        Assert.Equal("Reforma del baño", model.Title);
        Assert.Equal("Pago a 30 días", model.Notes);
        Assert.Equal(new DateOnly(2026, 12, 31), model.ValidUntil);
        Assert.Equal("Fontanería Marta Gil S.L.", model.Issuer.LegalName);
        Assert.Equal("B84531946", model.Issuer.TaxId);
        Assert.Equal("Calle de Alcalá 123", model.Issuer.AddressLine);
        Assert.Equal("28009", model.Issuer.PostalCode);
        Assert.Equal("Madrid", model.Issuer.City);
        Assert.Equal("Madrid", model.Issuer.Province);
        Assert.Equal("912345678", model.Issuer.Phone);
        Assert.Equal("marta@fontaneria-gil.example", model.Issuer.Email);
        Assert.Equal("Ana Pérez", model.Customer.Name);
        Assert.Equal("Calle Luna 3, 28001 Madrid", model.Customer.Address);
        Assert.Equal(2, model.Lines.Count);
        Assert.Equal(["Mano de obra", "Material"], model.Lines.Select(l => l.Description));
    }

    [Fact]
    public async Task TheAmountsAreTheOnesTheServerStoredNotRecalculated()
    {
        var (db, _, quote) = await SeedAsync();
        var renderer = new CapturingRenderer();

        await RunAsync(db, renderer, quote.Id);
        var model = renderer.Received.Single();

        Assert.Equal((quote.NetCents, quote.VatCents, quote.TotalCents), (model.NetCents, model.VatCents, model.TotalCents));
        Assert.Equal(quote.CalculateTotals().Breakdown.Select(b => (b.VatPercent, b.BaseCents, b.VatCents)), model.Breakdown.Select(b => (b.VatPercent, b.BaseCents, b.VatCents)));
        Assert.Equal(quote.Items.OrderBy(i => i.Position).Select(i => i.NetCents), model.Lines.Select(l => l.NetCents));
    }

    [Theory]
    [InlineData(QuoteStatus.Draft, true)]
    [InlineData(QuoteStatus.Sent, false)]
    public async Task OnlyADraftIsMarkedAsDraft(QuoteStatus status, bool expected)
    {
        var (db, _, quote) = await SeedAsync(status: status);
        var renderer = new CapturingRenderer();

        await RunAsync(db, renderer, quote.Id);

        Assert.Equal(expected, renderer.Received.Single().IsDraft);
    }

    [Fact]
    public async Task TheCustomerBringsOnlyItsNameAndItsAddressNeverItsNotesEmailOrPhone()
    {
        var (db, _, quote) = await SeedAsync();
        var renderer = new CapturingRenderer();

        await RunAsync(db, renderer, quote.Id);
        var model = renderer.Received.Single();

        // Se busca en todo lo que el generador recibe, no solo en la propiedad que se espera vacía.
        var everything = string.Join('|', new[] { model.Title, model.Notes, model.Customer.Name, model.Customer.Address, model.Issuer.LegalName, model.Issuer.AddressLine }.Concat(model.Lines.Select(l => l.Description)));
        Assert.DoesNotContain("NOTA-INTERNA", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("secreto@example.com", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("600123456", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomerWithoutAddressGivesADocumentWithoutAddress()
    {
        var (db, _, quote) = await SeedAsync(customerAddress: null);
        var renderer = new CapturingRenderer();

        await RunAsync(db, renderer, quote.Id);

        Assert.Null(renderer.Received.Single().Customer.Address);
    }

    // ---------- El orden de las comprobaciones (R12) ----------

    [Fact]
    public async Task AQuoteThatDoesNotExist_IsAnotFound_AndNothingIsRendered()
    {
        var (db, _, _) = await SeedAsync();
        var renderer = new CapturingRenderer();

        var missing = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(db, renderer, missing));

        Assert.Equal(QuoteErrors.NotFound, exception.Code);
        Assert.Empty(renderer.Received);
    }

    [Fact]
    public async Task WithoutAFiscalProfileThereIsNoPdf_AndNothingIsRendered()
    {
        var (db, _, quote) = await SeedAsync(withProfile: false);
        var renderer = new CapturingRenderer();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => RunAsync(db, renderer, quote.Id));

        Assert.Equal(QuoteErrors.CompanyProfileIncomplete, exception.Code);
        Assert.Empty(renderer.Received);
    }

    [Fact]
    public async Task ANonExistentQuoteWithoutProfileIsAnotFoundNotAnIncompleteProfile()
    {
        // El 404 va antes que el 400: a quien pregunta por un presupuesto que no es suyo no se le dice nada de los datos de la empresa.
        var (db, _, _) = await SeedAsync(withProfile: false);

        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(db, new CapturingRenderer(), Guid.NewGuid()));
    }

    [Fact]
    public async Task ItNeverWritesOrTracksAnything()
    {
        var (db, _, quote) = await SeedAsync();

        // Otro contexto sobre la misma base: es «la petición», y no arrastra lo que el alta de los datos dejó seguido en el primero.
        await using var request = TestDbContextFactory.CreateSharing(db);
        await new GetQuotePdfQueryHandler(request, new CapturingRenderer()).Handle(new GetQuotePdfQuery(quote.Id), CancellationToken.None);

        Assert.Equal(0, request.SaveCount);
        Assert.Empty(request.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TheQuoteIsNotModifiedByGeneratingItsPdf()
    {
        var (db, _, quote) = await SeedAsync(status: QuoteStatus.Draft);
        var before = (quote.Revision, quote.Status, quote.NetCents, quote.Items.Count);

        await RunAsync(db, new CapturingRenderer(), quote.Id);

        await using var other = TestDbContextFactory.CreateSharing(db);
        var stored = other.Quotes.Single(q => q.Id == quote.Id);
        Assert.Equal(before, (stored.Revision, stored.Status, stored.NetCents, other.Quotes.Where(q => q.Id == quote.Id).SelectMany(q => q.Items).Count()));
    }

    [Fact]
    public void TheResultNeverPrintsItsContent()
    {
        Assert.Equal("QuotePdfFile", new QuotePdfFile("presupuesto-P-0001.pdf", [1, 2, 3]).ToString());
    }

    [Fact]
    public void TheValidatorRejectsAnEmptyId()
    {
        var validator = new GetQuotePdfQueryValidator();

        Assert.False(validator.Validate(new GetQuotePdfQuery(Guid.Empty)).IsValid);
        Assert.True(validator.Validate(new GetQuotePdfQuery(Guid.NewGuid())).IsValid);
    }

    [Fact]
    public async Task IfTheRendererFailsTheErrorIsNotSwallowed()
    {
        var (db, _, quote) = await SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new GetQuotePdfQueryHandler(db, new ThrowingRenderer()).Handle(new GetQuotePdfQuery(quote.Id), CancellationToken.None));
    }

    private sealed class ThrowingRenderer : IQuotePdfRenderer
    {
        public byte[] Render(QuotePdfModel model) => throw new InvalidOperationException("fallo del generador");
    }
}

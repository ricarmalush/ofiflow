using OfiFlow.Domain.Common;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Domain.Tests.Quotes;

/// <summary>
/// El presupuesto (spec 023): lo que lo crea y lo cambia, las cotas de cada dato, y la máquina de estados <b>entera</b> (cada transición
/// permitida y cada una prohibida), porque es lo que decide que un precio ofrecido no se pueda reescribir (R4, R7, R8).
/// </summary>
public class QuoteTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private static QuoteItemInput Line(decimal quantity = 1, long price = 10000, int vat = 21, string description = "Mano de obra") =>
        new(description, quantity, price, vat);

    private static Quote Create(int number = 1, string title = "Reforma del baño", string? notes = null, DateOnly? validUntil = null, params QuoteItemInput[] items) =>
        Quote.Create(TenantId, CustomerId, null, number, title, notes, validUntil, items);

    private static Quote InStatus(QuoteStatus status)
    {
        var quote = Create(items: [Line()]);

        switch (status)
        {
            case QuoteStatus.Sent:
                quote.Send(Now);
                break;
            case QuoteStatus.Accepted:
                quote.Send(Now);
                quote.Accept(Now);
                break;
            case QuoteStatus.Rejected:
                quote.Send(Now);
                quote.Reject(Now);
                break;
        }

        Assert.Equal(status, quote.Status);
        return quote;
    }

    // ---------- Crear ----------

    [Fact]
    public void Create_StartsAsADraft_WithTheNumber_TheOwnersAndTheTotalsTheServerComputes()
    {
        var jobId = Guid.NewGuid();
        var quote = Quote.Create(TenantId, CustomerId, jobId, 7, "Reforma", "Condiciones", new DateOnly(2026, 12, 31), [Line(2.5m, 1234), Line(1, 5000, 0)]);

        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Equal(TenantId, quote.TenantId);
        Assert.Equal(CustomerId, quote.CustomerId);
        Assert.Equal(jobId, quote.JobId);
        Assert.Equal(7, quote.Number);
        Assert.Equal(new DateOnly(2026, 12, 31), quote.ValidUntil);
        Assert.Equal(3085 + 5000, quote.NetCents);
        Assert.Equal(648, quote.VatCents);
        Assert.Equal(3085 + 5000 + 648, quote.TotalCents);
        Assert.Null(quote.SentAt);
        Assert.Null(quote.DecidedAt);
    }

    [Fact]
    public void Create_KeepsTheLinesInTheOrderTheyArrived_WithTheirPosition()
    {
        var quote = Create(items: [Line(description: "C"), Line(description: "A"), Line(description: "B")]);

        Assert.Equal(["C", "A", "B"], quote.Items.OrderBy(i => i.Position).Select(i => i.Description));
        Assert.Equal([0, 1, 2], quote.Items.Select(i => i.Position));
        Assert.All(quote.Items, item =>
        {
            Assert.Equal(quote.Id, item.QuoteId);
            Assert.Equal(TenantId, item.TenantId);
        });
    }

    [Fact]
    public void Create_WithoutLines_IsAValidDraft_WithZeroTotals()
    {
        var quote = Create();

        Assert.Empty(quote.Items);
        Assert.Equal(0, quote.TotalCents);
    }

    [Fact]
    public void Create_TrimsTheTexts_AndAnEmptyNoteIsNoNote()
    {
        var quote = Create(title: "  Reforma  ", notes: "   ", items: [Line(description: "  Cable  ")]);

        Assert.Equal("Reforma", quote.Title);
        Assert.Null(quote.Notes);
        Assert.Equal("Cable", quote.Items.Single().Description);
    }

    [Fact]
    public void TheTotals_AreAlwaysTheCalculatorsResult_ForTheStoredLines()
    {
        var quote = Create(items: [Line(2.5m, 1234), Line(0.001m, 500, 10), Line(3, 2500, 4)]);

        var totals = quote.CalculateTotals();

        Assert.Equal(totals.NetCents, quote.NetCents);
        Assert.Equal(totals.VatCents, quote.VatCents);
        Assert.Equal(totals.TotalCents, quote.TotalCents);
    }

    [Fact]
    public void TheStoredTotals_CanOnlyBeSetByTheQuoteItself()
    {
        // R5: «la petición nunca manda un total». Ninguna propiedad de importe tiene un setter accesible desde fuera.
        foreach (var name in new[] { nameof(Quote.NetCents), nameof(Quote.VatCents), nameof(Quote.TotalCents), nameof(Quote.Number), nameof(Quote.Status), nameof(Quote.TenantId), nameof(Quote.Revision) })
        {
            var setter = typeof(Quote).GetProperty(name)!.SetMethod;
            Assert.True(setter is null || !setter.IsPublic, $"{name} no puede tener un setter público");
        }

        Assert.True(typeof(Quote).GetProperty(nameof(Quote.Items))!.PropertyType == typeof(IReadOnlyList<QuoteItem>));
    }

    // ---------- Cotas ----------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithANumberBelowOne_Throws(int number) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(number));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutTitle_Throws(string title) =>
        Assert.Throws<ArgumentException>(() => Create(title: title));

    [Fact]
    public void TheTitle_AcceptsExactlyTheMaximum_AndNoMore()
    {
        Create(title: new string('t', Quote.TitleMaxLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(title: new string('t', Quote.TitleMaxLength + 1)));
    }

    [Fact]
    public void TheNotes_AcceptExactlyTheMaximum_AndNoMore()
    {
        Create(notes: new string('n', Quote.NotesMaxLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(notes: new string('n', Quote.NotesMaxLength + 1)));
    }

    [Fact]
    public void TheValidUntil_AcceptsTheTwoEndsOfTheRange_AndNothingOutside()
    {
        Create(validUntil: new DateOnly(2000, 1, 1));
        Create(validUntil: new DateOnly(2099, 12, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(validUntil: new DateOnly(1999, 12, 31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(validUntil: new DateOnly(2100, 1, 1)));
    }

    [Fact]
    public void APastValidUntil_IsNotRejected_BecauseTheExpiryDoesNotBlockAnything()
    {
        // R9, D8: la caducidad es informativa.
        var quote = Create(validUntil: new DateOnly(2001, 1, 1));

        Assert.Equal(new DateOnly(2001, 1, 1), quote.ValidUntil);
    }

    [Fact]
    public void TheLines_AcceptExactlyTheMaximumCount_AndNoMore()
    {
        Create(items: Enumerable.Repeat(Line(), Quote.MaxItems).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(items: Enumerable.Repeat(Line(), Quote.MaxItems + 1).ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ALine_WithoutDescription_Throws(string description) =>
        Assert.Throws<ArgumentException>(() => Create(items: [Line(description: description)]));

    [Fact]
    public void TheDescription_AcceptsExactlyTheMaximum_AndNoMore()
    {
        Create(items: [Line(description: new string('d', Quote.DescriptionMaxLength))]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(items: [Line(description: new string('d', Quote.DescriptionMaxLength + 1))]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0004")]
    [InlineData("1.0001")]
    [InlineData("100000")]
    [InlineData("99999.9991")]
    public void AQuantity_ThatIsNotPositive_HasMoreThanThreeDecimals_OrPassesTheMaximum_Throws(string quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(items: [Line(quantity: decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture))]));

    [Theory]
    [InlineData("0.001")]
    [InlineData("1")]
    [InlineData("2.5")]
    [InlineData("99999.999")]
    public void AQuantity_InRange_IsAccepted(string quantity) =>
        Create(items: [Line(quantity: decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture))]);

    [Fact]
    public void ThePrice_AcceptsZeroAndTheMaximum_ButNotNegativeNorOver()
    {
        Create(items: [Line(price: 0)]);
        Create(items: [Line(price: Quote.UnitPriceCentsMax)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(items: [Line(price: -1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(items: [Line(price: Quote.UnitPriceCentsMax + 1)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(21)]
    public void EveryAllowedVatRate_IsAccepted(int vat) => Create(items: [Line(vat: vat)]);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(100)]
    public void AnyOtherVatRate_Throws(int vat) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(items: [Line(vat: vat)]));

    // ---------- Modificar ----------

    [Fact]
    public void Update_ReplacesTheHeaderAndAllTheLines_AndRecomputesTheTotals()
    {
        var quote = Create(items: [Line(1, 10000), Line(1, 20000)]);
        var jobId = Guid.NewGuid();

        quote.Update(jobId, "Otro título", "Notas", new DateOnly(2027, 1, 1), [Line(2, 1000, 10)]);

        Assert.Equal("Otro título", quote.Title);
        Assert.Equal("Notas", quote.Notes);
        Assert.Equal(jobId, quote.JobId);
        Assert.Equal(new DateOnly(2027, 1, 1), quote.ValidUntil);
        Assert.Single(quote.Items);
        Assert.Equal(0, quote.Items.Single().Position);
        Assert.Equal(2000, quote.NetCents);
        Assert.Equal(200, quote.VatCents);
        Assert.Equal(2200, quote.TotalCents);
    }

    [Fact]
    public void Update_NeverChangesTheCustomerTheNumberOrTheOwner()
    {
        var quote = Create(number: 5, items: [Line()]);

        quote.Update(null, "Nuevo", null, null, [Line()]);

        Assert.Equal(CustomerId, quote.CustomerId);
        Assert.Equal(5, quote.Number);
        Assert.Equal(TenantId, quote.TenantId);
    }

    [Fact]
    public void Update_WithInvalidLines_LeavesTheQuoteUntouched()
    {
        var quote = Create(items: [Line(1, 10000)]);
        var totalBefore = quote.TotalCents;

        Assert.Throws<ArgumentOutOfRangeException>(() => quote.Update(null, "Nuevo", null, null, [Line(vat: 13)]));

        Assert.Equal("Reforma del baño", quote.Title);
        Assert.Equal(totalBefore, quote.TotalCents);
        Assert.Single(quote.Items);
    }

    [Fact]
    public void EveryChange_RaisesTheRevision_SoTheRowAlwaysChanges()
    {
        // R10: una edición que solo cambia las líneas (o ni eso) tiene que tocar la fila del presupuesto para que el control de concurrencia la vigile.
        var quote = Create(items: [Line()]);
        var revision = quote.Revision;

        quote.Update(null, "Reforma del baño", null, null, [Line()]);
        Assert.True(quote.Revision > revision);

        revision = quote.Revision;
        quote.Send(Now);
        Assert.True(quote.Revision > revision);

        revision = quote.Revision;
        quote.Accept(Now);
        Assert.True(quote.Revision > revision);
    }

    // ---------- La máquina de estados ----------

    public static TheoryData<QuoteStatus, string, bool> Matrix()
    {
        var data = new TheoryData<QuoteStatus, string, bool>();

        foreach (var status in Enum.GetValues<QuoteStatus>())
        {
            data.Add(status, "Update", status == QuoteStatus.Draft);
            data.Add(status, "Delete", status == QuoteStatus.Draft);
            data.Add(status, "Send", status == QuoteStatus.Draft);
            data.Add(status, "Accept", status == QuoteStatus.Sent);
            data.Add(status, "Reject", status == QuoteStatus.Sent);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryOperation_IsAllowedFromItsOwnStateOnly(QuoteStatus status, string operation, bool allowed)
    {
        var quote = InStatus(status);

        var attempt = () =>
        {
            switch (operation)
            {
                case "Update": quote.Update(null, "Nuevo", null, null, [Line()]); break;
                case "Delete": quote.EnsureCanBeDeleted(); break;
                case "Send": quote.Send(Now); break;
                case "Accept": quote.Accept(Now); break;
                case "Reject": quote.Reject(Now); break;
                default: throw new InvalidOperationException(operation);
            }
        };

        if (allowed)
        {
            attempt();
            return;
        }

        var expectedCode = operation switch
        {
            "Update" => QuoteErrors.NotEditable,
            "Delete" => QuoteErrors.CannotDelete,
            "Send" => QuoteErrors.CannotSend,
            "Accept" => QuoteErrors.CannotAccept,
            _ => QuoteErrors.CannotReject
        };

        var exception = Assert.Throws<DomainException>(attempt);

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal([status], exception.Arguments);
    }

    [Fact]
    public void ARefusedOperation_ChangesNothing()
    {
        var quote = InStatus(QuoteStatus.Sent);
        var (title, total, revision, items, sentAt) = (quote.Title, quote.TotalCents, quote.Revision, quote.Items.Count, quote.SentAt);

        Assert.Throws<DomainException>(() => quote.Update(null, "Otro", null, null, [Line(9, 99999)]));
        Assert.Throws<DomainException>(() => quote.Send(Now.AddDays(1)));

        Assert.Equal((title, total, revision, items, sentAt), (quote.Title, quote.TotalCents, quote.Revision, quote.Items.Count, quote.SentAt));
        Assert.Equal(QuoteStatus.Sent, quote.Status);
    }

    [Fact]
    public void Send_WithoutLines_Throws_AndTheQuoteStaysADraft()
    {
        var quote = Create();

        var exception = Assert.Throws<DomainException>(() => quote.Send(Now));

        Assert.Equal(QuoteErrors.Empty, exception.Code);
        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Null(quote.SentAt);
    }

    [Fact]
    public void Send_RecordsTheInstant_AndAcceptingOrRejecting_RecordTheirs()
    {
        var sent = InStatus(QuoteStatus.Draft);
        sent.Send(Now);
        Assert.Equal(QuoteStatus.Sent, sent.Status);
        Assert.Equal(Now, sent.SentAt);
        Assert.Null(sent.DecidedAt);

        var accepted = InStatus(QuoteStatus.Sent);
        accepted.Accept(Now.AddHours(2));
        Assert.Equal(QuoteStatus.Accepted, accepted.Status);
        Assert.Equal(Now.AddHours(2), accepted.DecidedAt);

        var rejected = InStatus(QuoteStatus.Sent);
        rejected.Reject(Now.AddHours(3));
        Assert.Equal(QuoteStatus.Rejected, rejected.Status);
        Assert.Equal(Now.AddHours(3), rejected.DecidedAt);
    }

    [Theory]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public void AResolvedQuote_IsNeverReopened_NorResolvedAgain(QuoteStatus status)
    {
        var quote = InStatus(status);
        var decidedAt = quote.DecidedAt;

        Assert.Throws<DomainException>(() => quote.Accept(Now.AddDays(1)));
        Assert.Throws<DomainException>(() => quote.Reject(Now.AddDays(1)));
        Assert.Throws<DomainException>(() => quote.Send(Now.AddDays(1)));

        Assert.Equal(status, quote.Status);
        Assert.Equal(decidedAt, quote.DecidedAt);
    }

    [Fact]
    public void TheItems_OfASentQuote_CannotBeChangedThroughTheCollection()
    {
        // La inmutabilidad (R4) no depende de que todo el mundo se acuerde de llamar a Update: la colección que se expone no se puede modificar.
        var quote = InStatus(QuoteStatus.Sent);

        Assert.False(quote.Items is List<QuoteItem>);
        Assert.Throws<NotSupportedException>(() => ((IList<QuoteItem>)quote.Items).Add(null!));
    }

    [Fact]
    public void EveryQuoteStatus_IsCoveredByTheMatrix()
    {
        // Un centinela: un estado nuevo obliga a decidir qué se puede hacer en él (la matriz de arriba se completa a propósito).
        Assert.Equal(4, Enum.GetValues<QuoteStatus>().Length);
        Assert.Equal(Enum.GetValues<QuoteStatus>().Length * 5, Matrix().Count);
    }

    [Fact]
    public void TheIdentity_IsTheId()
    {
        var first = Create(items: [Line()]);
        var second = Create(items: [Line()]);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first, second);
        Assert.IsAssignableFrom<AggregateRoot>(first);
        Assert.IsAssignableFrom<ITenantOwned>(first);
        Assert.IsAssignableFrom<ITenantOwned>(first.Items.First());
    }
}

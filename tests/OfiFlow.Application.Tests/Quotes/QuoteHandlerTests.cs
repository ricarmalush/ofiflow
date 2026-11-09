using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Commands.AcceptQuote;
using OfiFlow.Application.Quotes.Commands.CreateQuote;
using OfiFlow.Application.Quotes.Commands.DeleteQuote;
using OfiFlow.Application.Quotes.Commands.RejectQuote;
using OfiFlow.Application.Quotes.Commands.SendQuote;
using OfiFlow.Application.Quotes.Commands.UpdateQuote;
using OfiFlow.Application.Quotes.Queries.GetQuote;
using OfiFlow.Application.Quotes.Queries.GetQuotes;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// Los casos de uso de los presupuestos (spec 023), con dobles: cada rama, que la petición <b>nunca</b> fija un total, que una operación confirma una
/// sola vez, y que un conflicto de concurrencia se traduce siempre a <c>quote.changed_meanwhile</c>. Cada comprobación de lo guardado lee con OTRO
/// contexto sobre la misma base. El filtro por empresa y la concurrencia real se prueban contra SQL Server en Infrastructure.
/// </summary>
public class QuoteHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private static QuoteItemInput Line(decimal quantity = 1, long price = 10000, int vat = 21, string description = "Mano de obra") => new(description, quantity, price, vat);

    private static async Task<(TestDbContext Db, Customer Customer)> SeedCustomerAsync()
    {
        var db = TestDbContextFactory.Create();
        var customer = Customer.Create(TenantId, CustomerType.Person, "Ana Pérez", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        return (db, customer);
    }

    private static async Task<Quote> SeedQuoteAsync(TestDbContext db, Guid customerId, QuoteStatus status = QuoteStatus.Draft, int number = 1, DateTime? createdAt = null, params QuoteItemInput[] items)
    {
        var quote = Quote.Create(TenantId, customerId, null, number, $"Presupuesto {number}", null, null, items.Length == 0 ? [Line()] : items);

        switch (status)
        {
            case QuoteStatus.Sent: quote.Send(Now); break;
            case QuoteStatus.Accepted: quote.Send(Now); quote.Accept(Now); break;
            case QuoteStatus.Rejected: quote.Send(Now); quote.Reject(Now); break;
        }

        if (createdAt is { } created)
        {
            quote.CreatedAt = created;
        }

        db.Quotes.Add(quote);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        return quote;
    }

    private static async Task<Quote> ReloadAsync(TestDbContext db, Guid id)
    {
        await using var other = TestDbContextFactory.CreateSharing(db);
        return await other.Quotes.AsNoTracking().Include(q => q.Items).FirstAsync(q => q.Id == id);
    }

    private static CreateQuoteCommandHandler Creator(TestDbContext db) => new(db, new FakeTenantContext(TenantId));

    private static CreateQuoteCommand NewQuote(Guid customerId, Guid? jobId = null, params QuoteItemInput[] items) =>
        new(customerId, jobId, "Reforma del baño", "Condiciones", new DateOnly(2026, 12, 31), items.Length == 0 ? [Line(2.5m, 1234), Line(1, 5000, 0)] : items);

    // ---------- Crear ----------

    [Fact]
    public async Task Create_StoresADraft_WithTheNumberAndTheTotalsTheServerComputes_InOneSave()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;

        var id = await Creator(db).Handle(NewQuote(customer.Id), CancellationToken.None);

        var stored = await ReloadAsync(db, id);
        Assert.Equal(QuoteStatus.Draft, stored.Status);
        Assert.Equal(1, stored.Number);
        Assert.Equal(TenantId, stored.TenantId);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(2, stored.Items.Count);
        Assert.Equal(3085 + 5000, stored.NetCents);
        Assert.Equal(648, stored.VatCents);
        Assert.Equal(3085 + 5000 + 648, stored.TotalCents);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task Create_AssignsTheNextNumber_EachTime()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        await SeedQuoteAsync(db, customer.Id, number: 1);
        await SeedQuoteAsync(db, customer.Id, number: 2);

        var id = await Creator(db).Handle(NewQuote(customer.Id), CancellationToken.None);

        Assert.Equal(3, (await ReloadAsync(db, id)).Number);
    }

    [Fact]
    public async Task Create_ForAnUnknownCustomer_IsNotFound_AndSavesNothing()
    {
        var (db, _) = await SeedCustomerAsync();
        await using var _ = db;

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => Creator(db).Handle(NewQuote(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(CustomerErrors.NotFound, exception.Code);
        Assert.Equal(0, db.SaveCount);
        Assert.Empty(await db.Quotes.ToListAsync());
    }

    [Fact]
    public async Task Create_WithAJobOfTheSameCustomer_LinksIt()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var job = Job.Create(TenantId, customer.Id, "Reparar", null, JobPriority.Normal);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        var id = await Creator(db).Handle(NewQuote(customer.Id, job.Id), CancellationToken.None);

        Assert.Equal(job.Id, (await ReloadAsync(db, id)).JobId);
    }

    [Fact]
    public async Task Create_WithAJobOfAnotherCustomer_IsRefused_AndSavesNothing()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var job = Job.Create(TenantId, Guid.NewGuid(), "De otro cliente", null, JobPriority.Normal);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => Creator(db).Handle(NewQuote(customer.Id, job.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.JobCustomerMismatch, exception.Code);
        Assert.Equal(0, db.SaveCount);
    }

    [Fact]
    public async Task Create_WithAnUnknownJob_IsNotFound()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => Creator(db).Handle(NewQuote(customer.Id, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(JobErrors.NotFound, exception.Code);
    }

    [Fact]
    public async Task Create_WhenTheNumberIsTaken_RetriesWithoutLeavingTheFailedAttemptBehind()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        db.ExceptionsOnNextSaves.Enqueue(new DbUpdateException("El número ya existe"));

        var id = await Creator(db).Handle(NewQuote(customer.Id), CancellationToken.None);

        // Dos intentos de guardar, y de verdad UN presupuesto con SUS líneas (ni una fila fantasma del intento fallido).
        Assert.Equal(2, db.SaveCount);
        await using var other = TestDbContextFactory.CreateSharing(db);
        var all = await other.Quotes.Include(q => q.Items).ToListAsync();
        Assert.Equal(id, Assert.Single(all).Id);
        Assert.Equal(2, all[0].Items.Count);
    }

    [Fact]
    public async Task Create_GivesUpAfterThreeFailedAttempts_WithItsOwnCode_AndLeavesNothing()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        for (var i = 0; i < CreateQuoteCommandHandler.MaxNumberAttempts; i++)
        {
            db.ExceptionsOnNextSaves.Enqueue(new DbUpdateException("El número ya existe"));
        }

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => Creator(db).Handle(NewQuote(customer.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.NumberUnavailable, exception.Code);
        Assert.Equal(3, db.SaveCount);
        Assert.Equal(3, CreateQuoteCommandHandler.MaxNumberAttempts);
        await using var other = TestDbContextFactory.CreateSharing(db);
        Assert.Empty(await other.Quotes.ToListAsync());
    }

    [Fact]
    public void TheCommands_NeverCarryATotal_ThePricesAndAmountsAreTheServers()
    {
        // R5: ninguna propiedad de importe total, neto o IVA llega en una petición de crear o modificar.
        foreach (var command in new[] { typeof(CreateQuoteCommand), typeof(UpdateQuoteCommand) })
        {
            var names = command.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Total", StringComparison.Ordinal) || n.Contains("Net", StringComparison.Ordinal) || n.Contains("Vat", StringComparison.Ordinal) || n.Contains("Number", StringComparison.Ordinal) || n.Contains("Tenant", StringComparison.Ordinal) || n.Contains("Status", StringComparison.Ordinal));
        }
    }

    // ---------- Modificar ----------

    private static UpdateQuoteCommand Edit(Quote quote, string? version = null, Guid? jobId = null, params QuoteItemInput[] items) =>
        new(quote.Id, version ?? QuoteVersion.Encode(quote.RowVersion), jobId, "Título nuevo", "Notas nuevas", new DateOnly(2027, 3, 1), items.Length == 0 ? [Line(2, 1000, 10)] : items);

    [Fact]
    public async Task Update_ReplacesTheHeaderAndTheLines_AndRecomputesTheTotals_InOneSave()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, items: [Line(1, 10000), Line(1, 20000), Line(1, 30000)]);

        await new UpdateQuoteCommandHandler(db).Handle(Edit(quote), CancellationToken.None);

        var stored = await ReloadAsync(db, quote.Id);
        Assert.Equal("Título nuevo", stored.Title);
        Assert.Equal(new DateOnly(2027, 3, 1), stored.ValidUntil);
        Assert.Equal(1, stored.Items.Count);
        Assert.Equal(2000, stored.NetCents);
        Assert.Equal(200, stored.VatCents);
        Assert.Equal(2200, stored.TotalCents);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task Update_WithAStaleVersion_IsRefused_AndChangesNothing()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new UpdateQuoteCommandHandler(db).Handle(Edit(quote, version: "AQIDBA=="), CancellationToken.None));

        Assert.Equal(QuoteErrors.ChangedMeanwhile, exception.Code);
        Assert.Equal(0, db.SaveCount);
        Assert.Equal("Presupuesto 1", (await ReloadAsync(db, quote.Id)).Title);
    }

    [Fact]
    public async Task Update_WhenTheRowChangesBetweenReadingAndSaving_IsTheSameError()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id);
        db.ExceptionOnSave = new DbUpdateConcurrencyException("Otra petición cambió la fila");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => new UpdateQuoteCommandHandler(db).Handle(Edit(quote), CancellationToken.None));

        Assert.Equal(QuoteErrors.ChangedMeanwhile, exception.Code);
    }

    [Theory]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public async Task Update_OfAQuoteThatIsNotADraft_IsRefused_AndTheQuoteIsUntouched(QuoteStatus status)
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, status);

        var exception = await Assert.ThrowsAsync<DomainException>(() => new UpdateQuoteCommandHandler(db).Handle(Edit(quote), CancellationToken.None));

        Assert.Equal(QuoteErrors.NotEditable, exception.Code);
        Assert.Equal(0, db.SaveCount);
        var stored = await ReloadAsync(db, quote.Id);
        Assert.Equal("Presupuesto 1", stored.Title);
        Assert.Equal(10000 + 2100, stored.TotalCents);
    }

    [Fact]
    public async Task Update_WithAJobOfAnotherCustomer_IsRefused_AndWithoutAJobItUnlinks()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var other = Job.Create(TenantId, Guid.NewGuid(), "De otro", null, JobPriority.Normal);
        var mine = Job.Create(TenantId, customer.Id, "Mío", null, JobPriority.Normal);
        db.Jobs.AddRange(other, mine);
        await db.SaveChangesAsync(CancellationToken.None);
        var quote = await SeedQuoteAsync(db, customer.Id);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => new UpdateQuoteCommandHandler(db).Handle(Edit(quote, jobId: other.Id), CancellationToken.None));
        Assert.Equal(QuoteErrors.JobCustomerMismatch, exception.Code);

        await new UpdateQuoteCommandHandler(db).Handle(Edit(quote, jobId: mine.Id), CancellationToken.None);
        Assert.Equal(mine.Id, (await ReloadAsync(db, quote.Id)).JobId);

        var fresh = await ReloadAsync(db, quote.Id);
        await new UpdateQuoteCommandHandler(db).Handle(Edit(fresh, jobId: null), CancellationToken.None);
        Assert.Null((await ReloadAsync(db, quote.Id)).JobId);
    }

    [Fact]
    public async Task Update_OfAnUnknownQuote_IsNotFound()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            new UpdateQuoteCommandHandler(db).Handle(Edit(quote) with { Id = Guid.NewGuid() }, CancellationToken.None));

        Assert.Equal(QuoteErrors.NotFound, exception.Code);
    }

    // ---------- Eliminar ----------

    [Fact]
    public async Task Delete_RemovesADraft_AndItsLines()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, items: [Line(), Line()]);

        await new DeleteQuoteCommandHandler(db).Handle(new DeleteQuoteCommand(quote.Id), CancellationToken.None);

        await using var other = TestDbContextFactory.CreateSharing(db);
        Assert.Empty(await other.Quotes.ToListAsync());
        Assert.Equal(1, db.SaveCount);
    }

    [Theory]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public async Task Delete_OfAQuoteThatIsNotADraft_IsRefused_AndKeepsIt(QuoteStatus status)
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, status);

        var exception = await Assert.ThrowsAsync<DomainException>(() => new DeleteQuoteCommandHandler(db).Handle(new DeleteQuoteCommand(quote.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.CannotDelete, exception.Code);
        Assert.Equal(0, db.SaveCount);
        Assert.Equal(status, (await ReloadAsync(db, quote.Id)).Status);
    }

    [Fact]
    public async Task Delete_WhenItIsSentMeanwhile_IsTheConcurrencyError()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id);
        db.ExceptionOnSave = new DbUpdateConcurrencyException("Se envió mientras tanto");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => new DeleteQuoteCommandHandler(db).Handle(new DeleteQuoteCommand(quote.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.ChangedMeanwhile, exception.Code);
    }

    [Fact]
    public async Task Delete_OfAnUnknownQuote_IsNotFound()
    {
        var (db, _) = await SeedCustomerAsync();
        await using var _ = db;

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => new DeleteQuoteCommandHandler(db).Handle(new DeleteQuoteCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(QuoteErrors.NotFound, exception.Code);
    }

    // ---------- Enviar, aceptar, rechazar ----------

    [Fact]
    public async Task Send_MovesADraftToSent_WithTheInstant_InOneSave()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id);

        await new SendQuoteCommandHandler(db, new FakeTimeProvider(Now)).Handle(new SendQuoteCommand(quote.Id), CancellationToken.None);

        var stored = await ReloadAsync(db, quote.Id);
        Assert.Equal(QuoteStatus.Sent, stored.Status);
        Assert.Equal(Now, stored.SentAt);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task Send_ADraftWithoutLines_IsRefused_AndStaysADraft()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var empty = Quote.Create(TenantId, customer.Id, null, 1, "Vacío", null, null, []);
        db.Quotes.Add(empty);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        var exception = await Assert.ThrowsAsync<DomainException>(() => new SendQuoteCommandHandler(db, new FakeTimeProvider(Now)).Handle(new SendQuoteCommand(empty.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.Empty, exception.Code);
        Assert.Equal(QuoteStatus.Draft, (await ReloadAsync(db, empty.Id)).Status);
    }

    [Theory]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public async Task Send_FromAnyStateButDraft_IsRefused(QuoteStatus status)
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, status);

        var exception = await Assert.ThrowsAsync<DomainException>(() => new SendQuoteCommandHandler(db, new FakeTimeProvider(Now)).Handle(new SendQuoteCommand(quote.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.CannotSend, exception.Code);
    }

    [Fact]
    public async Task Accept_AndReject_MoveASentQuote_WithTheirInstant()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var toAccept = await SeedQuoteAsync(db, customer.Id, QuoteStatus.Sent, number: 1);
        var toReject = await SeedQuoteAsync(db, customer.Id, QuoteStatus.Sent, number: 2);
        var later = new FakeTimeProvider(Now.AddHours(5));

        await new AcceptQuoteCommandHandler(db, later).Handle(new AcceptQuoteCommand(toAccept.Id), CancellationToken.None);
        await new RejectQuoteCommandHandler(db, later).Handle(new RejectQuoteCommand(toReject.Id), CancellationToken.None);

        var accepted = await ReloadAsync(db, toAccept.Id);
        var rejected = await ReloadAsync(db, toReject.Id);
        Assert.Equal(QuoteStatus.Accepted, accepted.Status);
        Assert.Equal(Now.AddHours(5), accepted.DecidedAt);
        Assert.Equal(QuoteStatus.Rejected, rejected.Status);
        Assert.Equal(Now.AddHours(5), rejected.DecidedAt);
    }

    [Theory]
    [InlineData(QuoteStatus.Draft)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public async Task Accept_AndReject_FromAnyStateButSent_AreRefused(QuoteStatus status)
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, status);
        var time = new FakeTimeProvider(Now);

        var accept = await Assert.ThrowsAsync<DomainException>(() => new AcceptQuoteCommandHandler(db, time).Handle(new AcceptQuoteCommand(quote.Id), CancellationToken.None));
        var reject = await Assert.ThrowsAsync<DomainException>(() => new RejectQuoteCommandHandler(db, time).Handle(new RejectQuoteCommand(quote.Id), CancellationToken.None));

        Assert.Equal(QuoteErrors.CannotAccept, accept.Code);
        Assert.Equal(QuoteErrors.CannotReject, reject.Code);
        Assert.Equal(status, (await ReloadAsync(db, quote.Id)).Status);
    }

    [Fact]
    public async Task TheTransitions_OnAnUnknownQuote_AreNotFound()
    {
        var (db, _) = await SeedCustomerAsync();
        await using var _ = db;
        var time = new FakeTimeProvider(Now);
        var id = Guid.NewGuid();

        foreach (var attempt in new Func<Task>[]
        {
            () => new SendQuoteCommandHandler(db, time).Handle(new SendQuoteCommand(id), CancellationToken.None),
            () => new AcceptQuoteCommandHandler(db, time).Handle(new AcceptQuoteCommand(id), CancellationToken.None),
            () => new RejectQuoteCommandHandler(db, time).Handle(new RejectQuoteCommand(id), CancellationToken.None)
        })
        {
            var exception = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal(QuoteErrors.NotFound, exception.Code);
        }
    }

    [Fact]
    public async Task TheTransitions_WhenTheRowChangesMeanwhile_AreTheConcurrencyError()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        // Cada intento sobre su propio presupuesto: un guardado que falla no deshace lo que el dominio ya cambió en la entidad seguida.
        var draft = await SeedQuoteAsync(db, customer.Id, number: 1);
        var toAccept = await SeedQuoteAsync(db, customer.Id, QuoteStatus.Sent, number: 2);
        var toReject = await SeedQuoteAsync(db, customer.Id, QuoteStatus.Sent, number: 3);
        var time = new FakeTimeProvider(Now);

        db.ExceptionOnSave = new DbUpdateConcurrencyException("Cambió mientras tanto");

        foreach (var attempt in new Func<Task>[]
        {
            () => new SendQuoteCommandHandler(db, time).Handle(new SendQuoteCommand(draft.Id), CancellationToken.None),
            () => new AcceptQuoteCommandHandler(db, time).Handle(new AcceptQuoteCommand(toAccept.Id), CancellationToken.None),
            () => new RejectQuoteCommandHandler(db, time).Handle(new RejectQuoteCommand(toReject.Id), CancellationToken.None)
        })
        {
            var exception = await Assert.ThrowsAsync<BusinessRuleException>(attempt);
            Assert.Equal(QuoteErrors.ChangedMeanwhile, exception.Code);
        }
    }

    // ---------- Leer ----------

    [Fact]
    public async Task GetQuote_ReturnsTheLinesInOrder_TheBreakdown_TheVersionAndTheCustomerName()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var quote = await SeedQuoteAsync(db, customer.Id, items: [Line(1, 10000, 21, "C"), Line(1, 5000, 0, "A"), Line(2, 1000, 21, "B")]);

        var dto = await new GetQuoteQueryHandler(db).Handle(new GetQuoteQuery(quote.Id), CancellationToken.None);

        Assert.Equal("Ana Pérez", dto.CustomerName);
        Assert.Equal("Draft", dto.Status);
        Assert.Equal(["C", "A", "B"], dto.Items.Select(i => i.Description));
        Assert.Equal([10000L, 5000L, 2000L], dto.Items.Select(i => i.NetCents));
        Assert.Equal([(0, 5000L, 0L), (21, 12000L, 2520L)], dto.VatBreakdown.Select(b => (b.VatPercent, b.BaseCents, b.VatCents)));
        Assert.Equal(17000, dto.NetCents);
        Assert.Equal(2520, dto.VatCents);
        Assert.Equal(19520, dto.TotalCents);
        Assert.Equal(QuoteVersion.Encode(quote.RowVersion), dto.Version);
    }

    [Fact]
    public async Task GetQuote_OfAnUnknownQuote_IsNotFound()
    {
        var (db, _) = await SeedCustomerAsync();
        await using var _ = db;

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => new GetQuoteQueryHandler(db).Handle(new GetQuoteQuery(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(QuoteErrors.NotFound, exception.Code);
    }

    [Fact]
    public async Task GetQuotes_IsPaged_NewestFirst_WithoutTheLines_AndCountsTheFilteredTotal()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        for (var n = 1; n <= 7; n++)
        {
            await SeedQuoteAsync(db, customer.Id, number: n, createdAt: Now.AddMinutes(n));
        }

        var first = await new GetQuotesQueryHandler(db).Handle(new GetQuotesQuery(null, null, 1, 3), CancellationToken.None);
        var last = await new GetQuotesQueryHandler(db).Handle(new GetQuotesQuery(null, null, 3, 3), CancellationToken.None);

        Assert.Equal([7, 6, 5], first.Items.Select(i => i.Number));
        Assert.Equal([1], last.Items.Select(i => i.Number));
        Assert.Equal(7, first.TotalCount);
        Assert.Equal(3, first.PageSize);
        Assert.Equal("Ana Pérez", first.Items[0].CustomerName);
        Assert.All(first.Items, item => Assert.IsType<QuoteListItemDto>(item));
    }

    [Fact]
    public async Task GetQuotes_FiltersByStatusAndByCustomer()
    {
        var (db, customer) = await SeedCustomerAsync();
        await using var _ = db;
        var other = Customer.Create(TenantId, CustomerType.Person, "Luis Gómez", null, null, null, null);
        db.Customers.Add(other);
        await db.SaveChangesAsync(CancellationToken.None);
        await SeedQuoteAsync(db, customer.Id, QuoteStatus.Draft, 1);
        await SeedQuoteAsync(db, customer.Id, QuoteStatus.Sent, 2);
        await SeedQuoteAsync(db, other.Id, QuoteStatus.Sent, 3);

        var sent = await new GetQuotesQueryHandler(db).Handle(new GetQuotesQuery(QuoteStatus.Sent, null, 1, 25), CancellationToken.None);
        var ofCustomer = await new GetQuotesQueryHandler(db).Handle(new GetQuotesQuery(null, customer.Id, 1, 25), CancellationToken.None);
        var both = await new GetQuotesQueryHandler(db).Handle(new GetQuotesQuery(QuoteStatus.Sent, other.Id, 1, 25), CancellationToken.None);

        Assert.Equal([3, 2], sent.Items.Select(i => i.Number).OrderDescending());
        Assert.Equal(2, ofCustomer.TotalCount);
        Assert.Equal([3], both.Items.Select(i => i.Number));
        Assert.Equal(1, both.TotalCount);
    }

    [Fact]
    public void TheDto_OrdersTheLinesByPosition_WhateverOrderTheyWereLoadedIn()
    {
        // La base de datos no garantiza el orden en que devuelve las líneas (su clave es un Guid): el DTO no puede depender de él. Se baraja la lista interna a mano.
        var quote = Quote.Create(TenantId, Guid.NewGuid(), null, 1, "Orden", null, null, [Line(description: "A"), Line(description: "B"), Line(description: "C"), Line(description: "D")]);
        var internalList = (List<QuoteItem>)typeof(Quote).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(quote)!;
        internalList.Reverse();
        Assert.Equal(["D", "C", "B", "A"], quote.Items.Select(i => i.Description)); // el escenario está de verdad desordenado

        var dto = QuoteDto.From(quote, "Ana Pérez");

        Assert.Equal(["A", "B", "C", "D"], dto.Items.Select(i => i.Description));
        Assert.Equal([0, 1, 2, 3], dto.Items.Select(i => i.Position));
    }
}

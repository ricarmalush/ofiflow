using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Commands.CreateQuote;
using OfiFlow.Application.Quotes.Commands.DeleteQuote;
using OfiFlow.Application.Quotes.Commands.SendQuote;
using OfiFlow.Application.Quotes.Commands.UpdateQuote;
using OfiFlow.Application.Quotes.Queries.GetQuote;
using OfiFlow.Application.Quotes.Queries.GetQuotes;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Quotes;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Los presupuestos contra SQL Server real (spec 023, bloque 1): el filtro por empresa (también al numerar y al listar), el índice único del número, la
/// concurrencia de verdad (dos ediciones, una edición contra un envío, varias creaciones a la vez), la ida y vuelta de decimales, fechas e importes, el
/// borrado en cascada de las líneas y que el listado <b>usa los índices</b>.
/// </summary>
public class QuotePersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    private ApplicationDbContext Open(Guid tenantId) => _fixture.CreateDbContext(tenantId);

    private static QuoteItemInput Line(decimal quantity = 1, long price = 10000, int vat = 21, string description = "Mano de obra") => new(description, quantity, price, vat);

    private async Task<Guid> SeedCustomerAsync(Guid tenantId, string name = "Ana Pérez")
    {
        var customer = Customer.Create(tenantId, CustomerType.Person, name, null, null, null, null);
        await using var db = Open(tenantId);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return customer.Id;
    }

    private async Task<Guid> CreateAsync(Guid tenantId, Guid customerId, params QuoteItemInput[] items)
    {
        await using var db = Open(tenantId);
        return await new CreateQuoteCommandHandler(db, new FixedTenantContext(tenantId)).Handle(
            new CreateQuoteCommand(customerId, null, "Reforma del baño", null, null, items.Length == 0 ? [Line(), Line(2, 5000, 10)] : items),
            CancellationToken.None);
    }

    private async Task<Quote> LoadAsync(Guid tenantId, Guid id)
    {
        await using var db = Open(tenantId);
        return await db.Quotes.AsNoTracking().Include(q => q.Items).FirstAsync(q => q.Id == id);
    }

    // ---------- Ida y vuelta ----------

    [Fact]
    public async Task EverythingSurvivesTheRoundTrip_DecimalsDatesAndHugeAmounts()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var jobId = Guid.NewGuid();
        var quote = Quote.Create(
            tenantId, customerId, jobId, 1, "Reforma del baño", "Condiciones de pago", new DateOnly(2026, 12, 31),
            [Line(99999.999m, Quote.UnitPriceCentsMax, 21, "El máximo"), Line(0.001m, 500, 4, "El mínimo"), Line(2.5m, 1234, 0, "Intermedio")]);
        quote.Send(Now);

        await using (var db = Open(tenantId))
        {
            db.Quotes.Add(quote);
            await db.SaveChangesAsync();
        }

        var stored = await LoadAsync(tenantId, quote.Id);

        Assert.Equal(QuoteStatus.Sent, stored.Status);
        Assert.Equal(jobId, stored.JobId);
        Assert.Equal(new DateOnly(2026, 12, 31), stored.ValidUntil);
        Assert.Equal(Now, stored.SentAt);
        Assert.Equal(DateTimeKind.Utc, stored.SentAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(quote.NetCents, stored.NetCents);
        Assert.Equal(quote.VatCents, stored.VatCents);
        Assert.Equal(quote.TotalCents, stored.TotalCents);
        Assert.True(stored.TotalCents > 999_999_000_000_000);
        Assert.Equal([99999.999m, 0.001m, 2.5m], stored.Items.OrderBy(i => i.Position).Select(i => i.Quantity));
        Assert.Equal([Quote.UnitPriceCentsMax, 500, 1234], stored.Items.OrderBy(i => i.Position).Select(i => i.UnitPriceCents));
        Assert.Equal(["El máximo", "El mínimo", "Intermedio"], stored.Items.OrderBy(i => i.Position).Select(i => i.Description));
        Assert.NotEmpty(stored.RowVersion);
    }

    [Fact]
    public async Task TheStoredTotals_AreExactlyWhatTheCalculatorGivesForTheStoredLines()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId, Line(2.5m, 1234), Line(1, 5000, 0), Line(3, 2500, 10));

        var stored = await LoadAsync(tenantId, id);
        var totals = stored.CalculateTotals();

        Assert.Equal((totals.NetCents, totals.VatCents, totals.TotalCents), (stored.NetCents, stored.VatCents, stored.TotalCents));
    }

    [Fact]
    public async Task EveryChange_ChangesTheRowVersion()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId);
        var before = (await LoadAsync(tenantId, id)).RowVersion;

        await using (var db = Open(tenantId))
        {
            var quote = await db.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);
            // Una edición que no cambia nada de lo que se ve: las mismas líneas y la misma cabecera. La fila tiene que cambiar igualmente (Revision).
            quote.Update(quote.JobId, quote.Title, quote.Notes, quote.ValidUntil, quote.Items.Select(i => new QuoteItemInput(i.Description, i.Quantity, i.UnitPriceCents, i.VatPercent)).ToList());
            await db.SaveChangesAsync();
        }

        Assert.NotEqual(before, (await LoadAsync(tenantId, id)).RowVersion);
    }

    // ---------- El filtro por empresa ----------

    [Fact]
    public async Task EachCompany_OnlySeesItsOwnQuotes_AndTheirLines()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantA);
        var customerB = await SeedCustomerAsync(tenantB, "Luis Gómez");
        var mine = await CreateAsync(tenantA, customerA);
        var theirs = await CreateAsync(tenantB, customerB);

        await using var readA = Open(tenantA);
        await using var readB = Open(tenantB);

        Assert.Equal([mine], await readA.Quotes.Select(q => q.Id).ToListAsync());
        Assert.Equal([theirs], await readB.Quotes.Select(q => q.Id).ToListAsync());
        Assert.Equal(2, await readA.Set<QuoteItem>().CountAsync());
        Assert.Empty(await readA.Quotes.Where(q => q.Id == theirs).ToListAsync());
    }

    [Fact]
    public async Task AQuoteOfAnotherCompany_IsTheSameNotFound_ForEveryOperation()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantA);
        var theirs = await CreateAsync(tenantA, customerA);
        var version = QuoteVersion.Encode((await LoadAsync(tenantA, theirs)).RowVersion);

        await using var db = Open(tenantB);
        var time = new FakeTimeProvider(Now);

        var attempts = new Func<Task>[]
        {
            () => new GetQuoteQueryHandler(db).Handle(new GetQuoteQuery(theirs), CancellationToken.None),
            () => new UpdateQuoteCommandHandler(db).Handle(new UpdateQuoteCommand(theirs, version, null, "Robado", null, null, [Line()]), CancellationToken.None),
            () => new DeleteQuoteCommandHandler(db).Handle(new DeleteQuoteCommand(theirs), CancellationToken.None),
            () => new SendQuoteCommandHandler(db, time).Handle(new SendQuoteCommand(theirs), CancellationToken.None)
        };

        foreach (var attempt in attempts)
        {
            var exception = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal(QuoteErrors.NotFound, exception.Code);
        }

        // Y no pasó nada en la empresa de verdad.
        var untouched = await LoadAsync(tenantA, theirs);
        Assert.Equal(QuoteStatus.Draft, untouched.Status);
        Assert.Equal("Reforma del baño", untouched.Title);
    }

    [Fact]
    public async Task ACustomerOfAnotherCompany_CannotBeQuoted()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerOfB = await SeedCustomerAsync(tenantB);

        await using var db = Open(tenantA);
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            new CreateQuoteCommandHandler(db, new FixedTenantContext(tenantA)).Handle(
                new CreateQuoteCommand(customerOfB, null, "Cruzado", null, null, [Line()]), CancellationToken.None));

        Assert.Equal(CustomerErrors.NotFound, exception.Code);
        await using var read = Open(tenantA);
        Assert.Empty(await read.Quotes.ToListAsync());
    }

    [Fact]
    public async Task TheListing_OnlyCountsAndShowsTheCompanysOwnQuotes()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantA);
        var customerB = await SeedCustomerAsync(tenantB);
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(tenantA, customerA);
        }

        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(tenantB, customerB);
        }

        await using var db = Open(tenantA);
        var page = await new GetQuotesQueryHandler(db).Handle(new GetQuotesQuery(null, null), CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(customerA, item.CustomerId));
    }

    // ---------- La numeración ----------

    [Fact]
    public async Task TheNumbers_AreCorrelativePerCompany_AndEveryCompanyStartsAtOne()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantA);
        var customerB = await SeedCustomerAsync(tenantB);

        var a1 = await CreateAsync(tenantA, customerA);
        var a2 = await CreateAsync(tenantA, customerA);
        var a3 = await CreateAsync(tenantA, customerA);
        var b1 = await CreateAsync(tenantB, customerB);

        Assert.Equal([1, 2, 3], new[] { a1, a2, a3 }.Select(id => LoadAsync(tenantA, id).Result.Number));
        Assert.Equal(1, (await LoadAsync(tenantB, b1)).Number);
    }

    [Fact]
    public async Task TheNumberOfACompany_NeverLooksAtTheOthersQuotes()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantA);
        var customerB = await SeedCustomerAsync(tenantB);
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(tenantB, customerB);
        }

        var first = await CreateAsync(tenantA, customerA);

        Assert.Equal(1, (await LoadAsync(tenantA, first)).Number);
    }

    [Fact]
    public async Task TheUniqueIndex_RefusesARepeatedNumberInTheSameCompany_ButNotInAnother()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantA);
        var customerB = await SeedCustomerAsync(tenantB);
        await CreateAsync(tenantA, customerA);

        await using (var db = Open(tenantA))
        {
            db.Quotes.Add(Quote.Create(tenantA, customerA, null, 1, "Repetido", null, null, []));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = Open(tenantB))
        {
            db.Quotes.Add(Quote.Create(tenantB, customerB, null, 1, "Mismo número, otra empresa", null, null, []));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task SeveralCreationsAtTheSameTime_GetDifferentNumbers_AndNoneFailsNorLeavesAnythingBehind()
    {
        // Varias rondas, cada una en una empresa nueva: tres peticiones lanzan a la vez, leen el mismo máximo y compiten por el mismo número.
        for (var round = 0; round < 6; round++)
        {
            var tenantId = Guid.NewGuid();
            var customerId = await SeedCustomerAsync(tenantId);

            var ids = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => CreateAsync(tenantId, customerId, Line(), Line(2, 500, 10)))));

            await using var read = Open(tenantId);
            var quotes = await read.Quotes.Include(q => q.Items).OrderBy(q => q.Number).ToListAsync();

            Assert.Equal([1, 2, 3], quotes.Select(q => q.Number));
            Assert.Equal(ids.Order(), quotes.Select(q => q.Id).Order());
            Assert.All(quotes, q => Assert.Equal(2, q.Items.Count)); // ninguna línea fantasma de un intento fallido
            Assert.Equal(6, await read.Set<QuoteItem>().CountAsync());
        }
    }

    [Fact]
    public async Task GetQuote_ReturnsTheLinesInTheOrderTheyWereWritten_NotInTheOrderOfTheirRandomIds()
    {
        // Las líneas tienen un Guid como clave: SQL Server las devuelve por ese orden si nadie se lo pide. Con doce líneas, que coincidan por azar es de 1 entre 479 millones.
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var descriptions = Enumerable.Range(1, 12).Select(n => $"Línea {n:00}").ToArray();
        var id = await CreateAsync(tenantId, customerId, descriptions.Select((d, i) => Line(1, 100 + i, 21, d)).ToArray());

        await using var db = Open(tenantId);
        var dto = await new GetQuoteQueryHandler(db).Handle(new GetQuoteQuery(id), CancellationToken.None);

        Assert.Equal(descriptions, dto.Items.Select(i => i.Description));
        Assert.Equal(Enumerable.Range(0, 12), dto.Items.Select(i => i.Position));
        Assert.Equal("Ana Pérez", dto.CustomerName);
        Assert.Equal(QuoteVersion.Encode((await LoadAsync(tenantId, id)).RowVersion), dto.Version);
    }

    // ---------- La concurrencia ----------

    [Fact]
    public async Task TwoEditionsWithTheSameVersion_OneWins_AndTheOtherLeavesNothing()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId);

        await using var first = Open(tenantId);
        await using var second = Open(tenantId);
        var a = await first.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);
        var b = await second.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);

        a.Update(null, "Gana A", null, null, [Line(1, 111)]);
        b.Update(null, "Gana B", null, null, [Line(1, 222), Line(1, 333)]);

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        var stored = await LoadAsync(tenantId, id);
        Assert.Equal("Gana A", stored.Title);
        Assert.Single(stored.Items);
        Assert.Equal(111, stored.Items.Single().UnitPriceCents);
    }

    [Fact]
    public async Task AnEditionAndAnEarlierSend_NeverLeaveASentQuoteWithChangedLines()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId, Line(1, 10000));

        await using var editing = Open(tenantId);
        await using var sending = Open(tenantId);
        var toEdit = await editing.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);
        var toSend = await sending.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);

        toSend.Send(Now);
        await sending.SaveChangesAsync(); // el envío gana

        toEdit.Update(null, "Reescrito", null, null, [Line(1, 1)]);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => editing.SaveChangesAsync());

        var stored = await LoadAsync(tenantId, id);
        Assert.Equal(QuoteStatus.Sent, stored.Status);
        Assert.Equal("Reforma del baño", stored.Title);
        Assert.Equal([10000L], stored.Items.Select(i => i.UnitPriceCents));
    }

    [Fact]
    public async Task AnEditionThatChangesNothingVisible_StillConflictsWithAnEarlierSend()
    {
        // La razón de ser de Revision: una edición que deja las mismas líneas y la misma cabecera no toca ninguna columna de negocio de la fila; sin Revision
        // no habría UPDATE sobre el presupuesto y el control de concurrencia no la vigilaría.
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId, Line(1, 10000));

        await using var editing = Open(tenantId);
        await using var sending = Open(tenantId);
        var toEdit = await editing.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);
        var toSend = await sending.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);

        toSend.Send(Now);
        await sending.SaveChangesAsync();

        toEdit.Update(toEdit.JobId, toEdit.Title, toEdit.Notes, toEdit.ValidUntil, [Line(1, 10000)]);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => editing.SaveChangesAsync());

        Assert.Equal(QuoteStatus.Sent, (await LoadAsync(tenantId, id)).Status);
    }

    [Fact]
    public async Task AnEditionAndALaterSend_LeaveTheEditedDraft_AndTheSendFails()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId, Line(1, 10000));

        await using var editing = Open(tenantId);
        await using var sending = Open(tenantId);
        var toEdit = await editing.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);
        var toSend = await sending.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);

        toEdit.Update(null, "Editado", null, null, [Line(1, 777)]);
        await editing.SaveChangesAsync(); // la edición gana

        toSend.Send(Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => sending.SaveChangesAsync());

        var stored = await LoadAsync(tenantId, id);
        Assert.Equal(QuoteStatus.Draft, stored.Status);
        Assert.Equal("Editado", stored.Title);
        Assert.Null(stored.SentAt);
    }

    [Fact]
    public async Task ADeleteAndASend_OnlyOneWins()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId);

        await using var deleting = Open(tenantId);
        await using var sending = Open(tenantId);
        var toDelete = await deleting.Quotes.SingleAsync(q => q.Id == id);
        var toSend = await sending.Quotes.Include(q => q.Items).SingleAsync(q => q.Id == id);

        toSend.Send(Now);
        await sending.SaveChangesAsync();

        deleting.Quotes.Remove(toDelete);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => deleting.SaveChangesAsync());

        Assert.Equal(QuoteStatus.Sent, (await LoadAsync(tenantId, id)).Status);
    }

    [Fact]
    public async Task AStaleVersion_IsRefusedByTheHandler_AndTheCurrentOneWorks()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId);
        var original = QuoteVersion.Encode((await LoadAsync(tenantId, id)).RowVersion);

        await using (var db = Open(tenantId))
        {
            await new UpdateQuoteCommandHandler(db).Handle(new UpdateQuoteCommand(id, original, null, "Primera", null, null, [Line()]), CancellationToken.None);
        }

        await using (var db = Open(tenantId))
        {
            var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new UpdateQuoteCommandHandler(db).Handle(new UpdateQuoteCommand(id, original, null, "Segunda, con la versión vieja", null, null, [Line()]), CancellationToken.None));
            Assert.Equal(QuoteErrors.ChangedMeanwhile, exception.Code);
        }

        Assert.Equal("Primera", (await LoadAsync(tenantId, id)).Title);

        var current = QuoteVersion.Encode((await LoadAsync(tenantId, id)).RowVersion);
        await using (var db = Open(tenantId))
        {
            await new UpdateQuoteCommandHandler(db).Handle(new UpdateQuoteCommand(id, current, null, "Tercera", null, null, [Line()]), CancellationToken.None);
        }

        Assert.Equal("Tercera", (await LoadAsync(tenantId, id)).Title);
    }

    [Fact]
    public async Task EditingAndSendingAtTheSameTime_NeverLeaveASentQuoteWithChangedLines()
    {
        // Las dos operaciones, de verdad a la vez y varias veces: sea quien sea el que gane, un presupuesto enviado conserva SUS líneas.
        for (var round = 0; round < 12; round++)
        {
            var tenantId = Guid.NewGuid();
            var customerId = await SeedCustomerAsync(tenantId);
            var id = await CreateAsync(tenantId, customerId, Line(1, 10000));
            var version = QuoteVersion.Encode((await LoadAsync(tenantId, id)).RowVersion);

            var edit = Task.Run(async () =>
            {
                await using var db = Open(tenantId);
                await new UpdateQuoteCommandHandler(db).Handle(new UpdateQuoteCommand(id, version, null, "Reescrito", null, null, [Line(1, 1)]), CancellationToken.None);
            });

            var send = Task.Run(async () =>
            {
                await using var db = Open(tenantId);
                await new SendQuoteCommandHandler(db, new FakeTimeProvider(Now)).Handle(new SendQuoteCommand(id), CancellationToken.None);
            });

            // Cualquiera de las dos puede perder: el error es de negocio (nunca un 500) y lo que queda es coherente.
            foreach (var task in new[] { edit, send })
            {
                try
                {
                    await task;
                }
                catch (Exception exception) when (exception is BusinessRuleException or DomainException)
                {
                }
            }

            var stored = await LoadAsync(tenantId, id);
            if (stored.Status == QuoteStatus.Sent)
            {
                Assert.Equal([10000L], stored.Items.Select(i => i.UnitPriceCents));
                Assert.Equal("Reforma del baño", stored.Title);
            }
            else
            {
                Assert.Equal(QuoteStatus.Draft, stored.Status);
                Assert.Equal([1L], stored.Items.Select(i => i.UnitPriceCents));
            }
        }
    }

    // ---------- Las líneas ----------

    [Fact]
    public async Task ReplacingTheLines_DeletesTheOldOnesAndInsertsTheNewOnes_InTheDatabase()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId, Line(1, 100), Line(1, 200), Line(1, 300));

        var version = QuoteVersion.Encode((await LoadAsync(tenantId, id)).RowVersion);
        await using (var db = Open(tenantId))
        {
            await new UpdateQuoteCommandHandler(db).Handle(new UpdateQuoteCommand(id, version, null, "Dos líneas", null, null, [Line(1, 900, 10, "Nueva A"), Line(2, 800, 4, "Nueva B")]), CancellationToken.None);
        }

        var stored = await LoadAsync(tenantId, id);
        Assert.Equal(["Nueva A", "Nueva B"], stored.Items.OrderBy(i => i.Position).Select(i => i.Description));
        await using var read = Open(tenantId);
        Assert.Equal(2, await read.Set<QuoteItem>().IgnoreQueryFilters().CountAsync(i => i.QuoteId == id));
    }

    [Fact]
    public async Task DeletingADraft_DeletesItsLinesToo_AndNoRowIsLeftBehind()
    {
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var id = await CreateAsync(tenantId, customerId);

        await using (var db = Open(tenantId))
        {
            await new DeleteQuoteCommandHandler(db).Handle(new DeleteQuoteCommand(id), CancellationToken.None);
        }

        await using var read = Open(tenantId);
        Assert.Empty(await read.Quotes.IgnoreQueryFilters().Where(q => q.Id == id).ToListAsync());
        Assert.Equal(0, await read.Set<QuoteItem>().IgnoreQueryFilters().CountAsync(i => i.QuoteId == id));
    }

    // ---------- El listado ----------

    [Fact]
    public async Task TheListing_IsPaged_NewestFirst_AndFiltersByStatusAndCustomer()
    {
        var tenantId = Guid.NewGuid();
        var ana = await SeedCustomerAsync(tenantId, "Ana Pérez");
        var luis = await SeedCustomerAsync(tenantId, "Luis Gómez");
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            ids.Add(await CreateAsync(tenantId, i % 2 == 0 ? ana : luis));
            await Task.Delay(15); // CreatedAt distinto: el orden es del más reciente al más antiguo
        }

        await using (var db = Open(tenantId))
        {
            await new SendQuoteCommandHandler(db, new FakeTimeProvider(Now)).Handle(new SendQuoteCommand(ids[1]), CancellationToken.None);
        }

        await using var read = Open(tenantId);
        var handler = new GetQuotesQueryHandler(read);

        var first = await handler.Handle(new GetQuotesQuery(null, null, 1, 3), CancellationToken.None);
        var last = await handler.Handle(new GetQuotesQuery(null, null, 3, 3), CancellationToken.None);
        var sent = await handler.Handle(new GetQuotesQuery(QuoteStatus.Sent, null), CancellationToken.None);
        var ofLuis = await handler.Handle(new GetQuotesQuery(null, luis), CancellationToken.None);

        Assert.Equal([7, 6, 5], first.Items.Select(i => i.Number));
        Assert.Equal([1], last.Items.Select(i => i.Number));
        Assert.Equal(7, first.TotalCount);
        Assert.Equal([2], sent.Items.Select(i => i.Number));
        Assert.Equal(3, ofLuis.TotalCount);
        Assert.All(ofLuis.Items, i => Assert.Equal("Luis Gómez", i.CustomerName));
        Assert.All(first.Items, i => Assert.Equal(DateTimeKind.Utc, i.CreatedAt.Kind));
    }

    [Fact]
    public async Task TheListingByStatus_AndByCustomer_UseTheirIndexes_WhenTheTableIsBig()
    {
        // Muchas empresas con muchos presupuestos y una de ellas con una parte: lo que se comprueba es que las consultas REALES del listado, con datos suficientes,
        // buscan por el índice que les corresponde (empresa primero) en vez de recorrer la tabla entera.
        var target = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(target);
        await using (var db = Open(target))
        {
            var rows = new List<Quote>(6000);
            for (var t = 0; t < 12; t++)
            {
                var tenant = t == 0 ? target : Guid.NewGuid();
                for (var n = 1; n <= 500; n++)
                {
                    var quote = Quote.Create(tenant, t == 0 && n % 50 == 0 ? customerId : Guid.NewGuid(), null, n, $"P{t}-{n}", null, null, [Line()]);
                    if (n % 5 == 0)
                    {
                        quote.Send(Now);
                    }

                    rows.Add(quote);
                }
            }

            db.Quotes.AddRange(rows);
            await db.SaveChangesAsync();
        }

        await using var read = Open(target);
        await read.Database.ExecuteSqlRawAsync("DBCC FREEPROCCACHE");
        var handler = new GetQuotesQueryHandler(read);

        var sent = await handler.Handle(new GetQuotesQuery(QuoteStatus.Sent, null), CancellationToken.None);
        var ofCustomer = await handler.Handle(new GetQuotesQuery(null, customerId), CancellationToken.None);
        Assert.Equal(100, sent.TotalCount);
        Assert.Equal(10, ofCustomer.TotalCount);

        var plans = await read.Database.SqlQuery<string>($"""
            SELECT CAST(qp.query_plan AS nvarchar(max)) AS [Value]
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
            WHERE st.text LIKE N'%FROM [[]Quotes]%' AND st.text NOT LIKE N'%dm_exec_cached_plans%'
            """).ToListAsync();

        var all = string.Join("\n", plans);
        Assert.True(all.Contains("IX_Quotes_TenantId_Status_CreatedAt", StringComparison.Ordinal), "El filtro por estado no usa su índice");
        Assert.True(all.Contains("IX_Quotes_TenantId_CustomerId", StringComparison.Ordinal), "El filtro por cliente no usa su índice");
    }

    [Fact]
    public async Task TheNumbersAndAmounts_AreStoredWithoutAnyCultureDependence()
    {
        // Un presupuesto con decimales leído con una cultura que usa la coma como separador sigue siendo el mismo (el dinero no depende del idioma del servidor).
        var tenantId = Guid.NewGuid();
        var customerId = await SeedCustomerAsync(tenantId);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            var id = await CreateAsync(tenantId, customerId, Line(1.333m, 300));
            var stored = await LoadAsync(tenantId, id);

            Assert.Equal(1.333m, stored.Items.Single().Quantity);
            Assert.Equal(484, stored.TotalCents);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

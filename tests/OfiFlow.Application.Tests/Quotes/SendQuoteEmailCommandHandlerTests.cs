using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Quotes;
using OfiFlow.Application.Quotes.Commands.SendQuoteEmail;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Quotes;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// Mandar un presupuesto por correo (spec 026 R1 a R10, R19, R20), con dobles: el orden de las comprobaciones, qué mensaje se construye, que <b>no se manda nada</b> en cada rechazo, que se envía antes de
/// anotar, que anotar no puede estropear un envío ya hecho y que ni la dirección ni nada del documento llegan a un registro. El filtro por empresa real se prueba contra SQL Server en el bloque de extremo a extremo.
/// </summary>
public class SendQuoteEmailCommandHandlerTests
{
    private const string Recipient = "cliente-marcado@ejemplo.es";
    private const string CompanyName = "Fontanería Marta Gil S.L.";
    private const string CompanyEmail = "marta@fontaneria-gil.example";
    private const string InternalNote = "NOTA-INTERNA-del-cliente";
    private const string QuoteTitle = "Título-secreto-del-presupuesto";

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTime SentAt = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 14, 12, 30, 0, DateTimeKind.Utc);

    private sealed class CapturingRenderer : IQuotePdfRenderer
    {
        public List<QuotePdfModel> Received { get; } = [];

        public byte[] Render(QuotePdfModel model)
        {
            Received.Add(model);
            return [0x25, 0x50, 0x44, 0x46, 0x2D];
        }
    }

    private sealed class Fixture
    {
        public required TestDbContext Db { get; init; }

        public required Quote Quote { get; init; }

        public FakeEmailSender Sender { get; } = new(isEnabled: true);

        public CapturingRenderer Renderer { get; } = new();

        public ListLogger<SendQuoteEmailCommandHandler> Logger { get; } = new();

        public FakeTimeProvider Time { get; } = new(Now);

        public SendQuoteEmailCommandHandler Handler() => new(
            Db,
            new QuotePdfBuilder(Db, Renderer),
            Sender,
            new FakeCurrentUser(TenantRole.Owner, UserId, TenantId),
            new FakeTenantContext(TenantId),
            Time,
            Logger);

        public Task<QuoteEmailSentDto> SendAsync(string recipient = Recipient) =>
            Handler().Handle(new SendQuoteEmailCommand(Quote.Id, recipient), CancellationToken.None);
    }

    private static async Task<Fixture> SeedAsync(
        bool withProfile = true,
        bool withCompanyEmail = true,
        bool withItems = true,
        bool draft = false,
        int number = 12,
        DateOnly? validUntil = null)
    {
        var db = TestDbContextFactory.Create();
        var customer = Customer.Create(TenantId, CustomerType.Person, "Ana Pérez", Email.Create("ana-privada@example.com"), PhoneNumber.Create("600123456"), "Calle Luna 3", InternalNote);
        db.Customers.Add(customer);

        var items = withItems
            ? new[] { new QuoteItemInput("Mano de obra", 2.5m, 1234, 21), new QuoteItemInput("Material", 1m, 5000, 0) }
            : [];
        var quote = Quote.Create(TenantId, customer.Id, null, number, QuoteTitle, "Pago a 30 días", validUntil ?? new DateOnly(2026, 12, 31), items);
        if (!draft && withItems)
        {
            quote.Send(SentAt);
        }

        db.Quotes.Add(quote);

        if (withProfile)
        {
            db.CompanyProfiles.Add(CompanyProfile.Create(
                TenantId, CompanyName, SpanishTaxId.Create("B84531946"), "Calle de Alcalá 123", SpanishPostalCode.Create("28009"), "Madrid", "Madrid",
                PhoneNumber.Create("912345678"), withCompanyEmail ? Email.Create(CompanyEmail) : null));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ResetSaveCount();

        return new Fixture { Db = db, Quote = quote };
    }

    private static async Task<Quote> ReloadAsync(Fixture fixture) =>
        await fixture.Db.Quotes.AsNoTracking().SingleAsync(q => q.Id == fixture.Quote.Id);

    // ===== Camino feliz =====

    [Fact]
    public async Task ASentQuote_IsMailedWithTheFixedTemplate_AndThePdfAttached()
    {
        var fixture = await SeedAsync();

        var result = await fixture.SendAsync();

        var message = Assert.Single(fixture.Sender.Sent);
        Assert.Equal(Recipient, message.To.Value);
        Assert.Equal("Presupuesto P-0012", message.Subject);
        Assert.Contains(CompanyName, message.TextBody, StringComparison.Ordinal);
        Assert.Contains("P-0012", message.TextBody, StringComparison.Ordinal);
        Assert.Equal("presupuesto-P-0012.pdf", message.Attachment!.FileName);
        Assert.Equal("application/pdf", message.Attachment.ContentType);
        Assert.Equal([0x25, 0x50, 0x44, 0x46, 0x2D], message.Attachment.Content);
        Assert.Equal(Now, result.EmailedAt);
    }

    [Fact]
    public async Task TheTotalInTheBody_IsTheOneTheServerStored_NotRecalculated()
    {
        var fixture = await SeedAsync();
        var stored = fixture.Quote.TotalCents;

        await fixture.SendAsync();

        Assert.Contains(MoneyFormat.Format(stored), fixture.Sender.Sent.Single().TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePdfSentIsTheSameModelTheDownloadUses()
    {
        var fixture = await SeedAsync();

        await fixture.SendAsync();

        var model = Assert.Single(fixture.Renderer.Received);
        Assert.Equal("P-0012", model.Label);
        Assert.Equal(CompanyName, model.Issuer.LegalName);
        Assert.False(model.IsDraft);
    }

    [Fact]
    public async Task ADraft_IsMailedToo_WithTheDraftMarkInThePdf_AndStaysADraft()
    {
        var fixture = await SeedAsync(draft: true);

        await fixture.SendAsync();

        Assert.Single(fixture.Sender.Sent);
        Assert.True(fixture.Renderer.Received.Single().IsDraft);
        Assert.Equal(QuoteStatus.Draft, (await ReloadAsync(fixture)).Status);
    }

    [Fact]
    public async Task Mailing_DoesNotChangeTheStatus_TheRevisionOrTheContent()
    {
        var fixture = await SeedAsync();
        var before = await ReloadAsync(fixture);

        await fixture.SendAsync();

        var after = await ReloadAsync(fixture);
        Assert.Equal(QuoteStatus.Sent, after.Status);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.SentAt, after.SentAt);
        Assert.Equal(before.TotalCents, after.TotalCents);
        Assert.Equal(before.Title, after.Title);
    }

    [Fact]
    public async Task TheLastMailing_IsRecordedWithTheMomentAndThePerson_AndOnlyThat()
    {
        var fixture = await SeedAsync();

        await fixture.SendAsync();

        var quote = await ReloadAsync(fixture);
        Assert.Equal(Now, quote.EmailedAt);
        Assert.Equal(UserId, quote.EmailedByUserId);
        Assert.Equal(1, fixture.Db.SaveCount);
    }

    [Fact]
    public async Task AResend_ReplacesTheRecord_AndSendsAnotherMessage()
    {
        var fixture = await SeedAsync();
        await fixture.SendAsync();
        fixture.Time.Advance(TimeSpan.FromHours(2));

        var second = await fixture.SendAsync("otro@ejemplo.es");

        Assert.Equal(2, fixture.Sender.Sent.Count);
        Assert.Equal(Now.AddHours(2), second.EmailedAt);
        Assert.Equal(Now.AddHours(2), (await ReloadAsync(fixture)).EmailedAt);
    }

    [Fact]
    public async Task TheSurroundingSpacesOfTheAddress_AreTrimmed()
    {
        var fixture = await SeedAsync();

        await fixture.SendAsync("  " + Recipient + "\t");

        Assert.Equal(Recipient, fixture.Sender.Sent.Single().To.Value);
    }

    [Fact]
    public async Task ACompanyEmail_BecomesTheReplyTo_AndWithoutOneThereIsNoReplyTo()
    {
        var with = await SeedAsync(withCompanyEmail: true);
        var without = await SeedAsync(withCompanyEmail: false);

        await with.SendAsync();
        await without.SendAsync();

        Assert.Equal(CompanyEmail, with.Sender.Sent.Single().ReplyTo!.Value);
        Assert.Null(without.Sender.Sent.Single().ReplyTo);
    }

    [Fact]
    public async Task NothingTheUserTypedInTheQuoteOrTheCustomerReachesTheMessage()
    {
        var fixture = await SeedAsync();

        await fixture.SendAsync();

        var message = fixture.Sender.Sent.Single();
        var text = message.Subject + "\n" + message.TextBody;
        foreach (var secret in new[] { QuoteTitle, "Pago a 30 días", "Ana Pérez", InternalNote, "ana-privada@example.com", "600123456", "Calle Luna", "Mano de obra" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    // ===== Rechazos: no se manda nada =====

    [Fact]
    public async Task WithoutAMailServer_ItIsABusinessError_AndNothingIsReadOrGenerated()
    {
        var fixture = await SeedAsync();
        fixture.Sender.IsEnabled = false;

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => fixture.SendAsync());

        Assert.Equal(QuoteErrors.EmailDisabled, exception.Code);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Empty(fixture.Renderer.Received);
        Assert.Equal(0, fixture.Db.SaveCount);
    }

    [Fact]
    public async Task AQuoteThatDoesNotExist_Is404_AndNothingIsSent()
    {
        var fixture = await SeedAsync();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Handler().Handle(new SendQuoteEmailCommand(Guid.NewGuid(), Recipient), CancellationToken.None));

        Assert.Equal(QuoteErrors.NotFound, exception.Code);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Empty(fixture.Renderer.Received);
    }

    [Fact]
    public async Task AQuoteWithoutLines_IsRefused_AndNothingIsGenerated()
    {
        var fixture = await SeedAsync(withItems: false);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => fixture.SendAsync());

        Assert.Equal(QuoteErrors.EmailRequiresItems, exception.Code);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Empty(fixture.Renderer.Received);
    }

    [Fact]
    public async Task WithoutAFiscalProfile_ItIsRefused_AndNothingIsSent()
    {
        var fixture = await SeedAsync(withProfile: false);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => fixture.SendAsync());

        Assert.Equal(QuoteErrors.CompanyProfileIncomplete, exception.Code);
        Assert.Empty(fixture.Sender.Sent);
        Assert.Empty(fixture.Renderer.Received);
    }

    [Fact]
    public async Task TheOrderOfTheChecks_IsDisabled_Then404_Then_NoLines_Then_NoProfile()
    {
        // Sin líneas y sin perfil a la vez: gana «sin líneas» (va antes, R7).
        var both = await SeedAsync(withItems: false, withProfile: false);
        Assert.Equal(QuoteErrors.EmailRequiresItems, (await Assert.ThrowsAsync<BusinessRuleException>(() => both.SendAsync())).Code);

        // Sin servidor de correo y sin líneas: gana «sin servidor» (va antes).
        both.Sender.IsEnabled = false;
        Assert.Equal(QuoteErrors.EmailDisabled, (await Assert.ThrowsAsync<BusinessRuleException>(() => both.SendAsync())).Code);

        // Sin presupuesto y sin perfil: gana el 404 (no se dice nada de la empresa a quien pregunta por un presupuesto que no es suyo).
        var none = await SeedAsync(withProfile: false);
        await Assert.ThrowsAsync<NotFoundException>(() => none.Handler().Handle(new SendQuoteEmailCommand(Guid.NewGuid(), Recipient), CancellationToken.None));
    }

    // ===== El orden: se envía antes de anotar, y anotar no estropea el envío =====

    [Theory]
    [InlineData(EmailFailureCategory.Timeout)]
    [InlineData(EmailFailureCategory.Authentication)]
    [InlineData(EmailFailureCategory.Rejected)]
    [InlineData(EmailFailureCategory.Unavailable)]
    [InlineData(EmailFailureCategory.Unknown)]
    public async Task WhenTheServerDoesNotAccept_ItIsA502_AndNothingIsRecorded(EmailFailureCategory category)
    {
        var fixture = await SeedAsync();
        fixture.Sender.FailWith = category;

        var exception = await Assert.ThrowsAsync<ExternalServiceException>(() => fixture.SendAsync());

        Assert.Equal(QuoteErrors.EmailNotSent, exception.Code);
        Assert.Null((await ReloadAsync(fixture)).EmailedAt);
        Assert.Null((await ReloadAsync(fixture)).EmailedByUserId);
        Assert.Equal(0, fixture.Db.SaveCount);
    }

    [Fact]
    public async Task ItIsSentBeforeItIsRecorded()
    {
        var fixture = await SeedAsync();
        DateTime? recordedWhenSending = DateTime.MaxValue;
        fixture.Sender.OnSend = async () => recordedWhenSending = (await ReloadAsync(fixture)).EmailedAt;

        await fixture.SendAsync();

        Assert.Null(recordedWhenSending);
        Assert.NotNull((await ReloadAsync(fixture)).EmailedAt);
    }

    [Fact]
    public async Task WhenRecordingConflictsWithAConcurrentChange_TheMailAlreadySent_IsStillA200()
    {
        var fixture = await SeedAsync();
        fixture.Db.ExceptionOnSave = new DbUpdateConcurrencyException();

        var result = await fixture.SendAsync();

        Assert.Equal(Now, result.EmailedAt);
        Assert.Single(fixture.Sender.Sent);
        var entry = Assert.Single(fixture.Logger.Entries, e => e.EventId.Id == SecurityEventIds.QuoteEmailSent);
        Assert.Contains("fecha anotada False", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheDraftWasDeletedMeanwhile_TheMailAlreadySent_IsStillA200()
    {
        var fixture = await SeedAsync(draft: true);
        fixture.Sender.OnSend = async () =>
        {
            fixture.Db.Quotes.Remove(await fixture.Db.Quotes.SingleAsync(q => q.Id == fixture.Quote.Id));
            await fixture.Db.SaveChangesAsync(CancellationToken.None);
        };

        var result = await fixture.SendAsync();

        Assert.Equal(Now, result.EmailedAt);
        var entry = Assert.Single(fixture.Logger.Entries, e => e.EventId.Id == SecurityEventIds.QuoteEmailSent);
        Assert.Contains("fecha anotada False", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyOtherDatabaseFailureWhileRecording_IsNotSwallowed()
    {
        var fixture = await SeedAsync();
        fixture.Db.ExceptionOnSave = new InvalidOperationException("fallo de la base de datos");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SendAsync());
    }

    // ===== El registro: solo Ids y categoría =====

    [Fact]
    public async Task ASuccessfulMailing_LeavesOneEvent1410_WithTheIdsAndNothingElse()
    {
        var fixture = await SeedAsync();

        await fixture.SendAsync();

        var entry = Assert.Single(fixture.Logger.Entries, e => e.EventId.Id == SecurityEventIds.QuoteEmailSent);
        Assert.Equal(1410, entry.EventId.Id);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.Contains(fixture.Quote.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(TenantId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(UserId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("fecha anotada True", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedMailing_LeavesOneEvent1411_WithTheCategoryAndNoAddress()
    {
        var fixture = await SeedAsync();
        fixture.Sender.FailWith = EmailFailureCategory.Authentication;

        await Assert.ThrowsAsync<ExternalServiceException>(() => fixture.SendAsync());

        var entry = Assert.Single(fixture.Logger.Entries, e => e.EventId.Id == SecurityEventIds.QuoteEmailFailed);
        Assert.Equal(1411, entry.EventId.Id);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("Authentication", entry.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.Quote.Id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Logger.Entries, e => e.EventId.Id == SecurityEventIds.QuoteEmailSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoLogEntry_CarriesTheAddress_TheCompany_TheTitleOrTheFileName(bool fails)
    {
        var fixture = await SeedAsync();
        if (fails)
        {
            fixture.Sender.FailWith = EmailFailureCategory.Rejected;
        }

        try
        {
            await fixture.SendAsync();
        }
        catch (ExternalServiceException)
        {
            // Esperado cuando falla: lo que se comprueba es el registro.
        }

        Assert.NotEmpty(fixture.Logger.Entries);
        foreach (var (_, _, message) in fixture.Logger.Entries)
        {
            foreach (var secret in new[] { Recipient, "ejemplo.es", CompanyName, CompanyEmail, QuoteTitle, "presupuesto-P-0012", "Ana", InternalNote })
            {
                Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheCommandPrintsNothingOfItsContent()
    {
        var command = new SendQuoteEmailCommand(Guid.NewGuid(), Recipient);

        Assert.DoesNotContain("marcado", command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ejemplo", command.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommand_NeedsTheIssuePermission()
    {
        var attribute = typeof(SendQuoteEmailCommand).GetCustomAttributes(typeof(OfiFlow.Application.Common.Authorization.RequiresPermissionAttribute), inherit: false);

        Assert.Equal(Permission.QuotesIssue, ((OfiFlow.Application.Common.Authorization.RequiresPermissionAttribute)Assert.Single(attribute)).Permission);
    }
}

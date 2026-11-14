using OfiFlow.Domain.Quotes;

namespace OfiFlow.Domain.Tests.Quotes;

/// <summary>
/// La anotación del último envío por correo (spec 026 R19, D4): vale en <b>cualquier estado</b>, no es un cambio del contenido (no sube la revisión, no toca líneas ni totales: un presupuesto
/// enviado sigue siendo inmutable) y cada envío sustituye al anterior. No guarda la dirección: el modelo no tiene dónde.
/// </summary>
public class QuoteEmailedTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTime Sent = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mailed = new(2026, 11, 14, 12, 30, 0, DateTimeKind.Utc);

    private static Quote InStatus(QuoteStatus status)
    {
        var quote = Quote.Create(TenantId, Guid.NewGuid(), null, 1, "Reforma", null, null, [new QuoteItemInput("Mano de obra", 1, 10000, 21)]);

        switch (status)
        {
            case QuoteStatus.Sent:
                quote.Send(Sent);
                break;
            case QuoteStatus.Accepted:
                quote.Send(Sent);
                quote.Accept(Sent);
                break;
            case QuoteStatus.Rejected:
                quote.Send(Sent);
                quote.Reject(Sent);
                break;
        }

        return quote;
    }

    [Fact]
    public void ANewQuote_HasNeverBeenMailed()
    {
        var quote = InStatus(QuoteStatus.Draft);

        Assert.Null(quote.EmailedAt);
        Assert.Null(quote.EmailedByUserId);
    }

    [Theory]
    [InlineData(QuoteStatus.Draft)]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public void ItIsRecordedInEveryStatus_WithoutChangingIt(QuoteStatus status)
    {
        var quote = InStatus(status);

        quote.RecordEmailSent(Mailed, UserId);

        Assert.Equal(Mailed, quote.EmailedAt);
        Assert.Equal(UserId, quote.EmailedByUserId);
        Assert.Equal(status, quote.Status);
    }

    [Theory]
    [InlineData(QuoteStatus.Draft)]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public void ItIsNotAChangeOfTheContent_NoRevisionNoLinesNoTotals(QuoteStatus status)
    {
        var quote = InStatus(status);
        var revision = quote.Revision;
        var total = quote.TotalCents;
        var items = quote.Items.Count;
        var sentAt = quote.SentAt;
        var decidedAt = quote.DecidedAt;

        quote.RecordEmailSent(Mailed, UserId);

        Assert.Equal(revision, quote.Revision);
        Assert.Equal(total, quote.TotalCents);
        Assert.Equal(items, quote.Items.Count);
        Assert.Equal(sentAt, quote.SentAt);
        Assert.Equal(decidedAt, quote.DecidedAt);
    }

    [Fact]
    public void ASentQuote_StaysImmutable_AfterBeingMailed()
    {
        var quote = InStatus(QuoteStatus.Sent);
        quote.RecordEmailSent(Mailed, UserId);

        Assert.Throws<OfiFlow.Domain.Common.DomainException>(() => quote.Update(null, "Otro título", null, null, [new QuoteItemInput("X", 1, 1, 21)]));
    }

    [Fact]
    public void EachMailing_ReplacesThePreviousOne()
    {
        var quote = InStatus(QuoteStatus.Sent);
        var other = Guid.NewGuid();
        quote.RecordEmailSent(Mailed, UserId);

        quote.RecordEmailSent(Mailed.AddDays(1), other);

        Assert.Equal(Mailed.AddDays(1), quote.EmailedAt);
        Assert.Equal(other, quote.EmailedByUserId);
    }

    [Fact]
    public void WithoutAPerson_ItIsAProgrammingError() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InStatus(QuoteStatus.Sent).RecordEmailSent(Mailed, Guid.Empty));

    [Fact]
    public void TheQuoteHasNoPlaceToKeepAnAddress()
    {
        // Spec 026 D4: se guarda cuándo y quién, nunca a quién. El modelo no tiene ninguna propiedad de texto relacionada con el correo.
        var properties = typeof(Quote).GetProperties().Where(p => p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).Order().ToList();

        Assert.Equal(["EmailedAt", "EmailedByUserId"], properties);
    }
}

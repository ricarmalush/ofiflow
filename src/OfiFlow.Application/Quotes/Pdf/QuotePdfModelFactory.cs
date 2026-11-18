using OfiFlow.Application.Company;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Pdf;

/// <summary>
/// Construye lo que se imprime a partir de lo que el servidor ya tiene calculado: el <see cref="QuoteDto"/> (importes, desglose y líneas, spec 023 R5) y el perfil fiscal (spec 024). <b>No suma,
/// no multiplica ni redondea nada</b> (spec 025 R5): copia los importes tal cual, de modo que el PDF no puede discrepar de la pantalla. Es una función pura: no lee la base de datos ni la hora.
/// </summary>
public static class QuotePdfModelFactory
{
    /// <summary>
    /// La zona horaria de la fecha del documento (D12). La plataforma es solo para España (D6 de la spec 024): en UTC, un presupuesto creado a las 00:30 de Madrid saldría con el día anterior.
    /// Hace falta la base de datos de zonas horarias en el servidor (en una imagen mínima de Linux hay que instalarla).
    /// </summary>
    public const string TimeZoneId = "Europe/Madrid";

    private static readonly Lazy<TimeZoneInfo> Madrid = new(() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

    /// <param name="quote">El presupuesto, tal como lo devuelve <c>GetQuote</c>.</param>
    /// <param name="company">El perfil fiscal de la empresa. Tiene que estar completo: si no, el caso de uso responde <c>quote.company_profile_incomplete</c> antes de llegar aquí.</param>
    /// <param name="customerAddress">La dirección del cliente, si la tiene. Solo esto y su nombre salen en el documento.</param>
    public static QuotePdfModel Create(QuoteDto quote, CompanyProfileDto company, string? customerAddress)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(company);

        // Un perfil incompleto no es un caso de negocio aquí (eso lo decide quien llama, con su código de error): llegar con uno es un error de programación.
        if (!company.IsComplete
            || company.LegalName is not { } legalName
            || company.TaxId is not { } taxId
            || company.AddressLine is not { } addressLine
            || company.PostalCode is not { } postalCode
            || company.City is not { } city)
        {
            throw new ArgumentException("El perfil fiscal tiene que estar completo para generar el PDF.", nameof(company));
        }

        var issuer = new QuotePdfIssuer(legalName, taxId, addressLine, postalCode, city, company.Province, company.Phone, company.Email);

        return new QuotePdfModel(
            issuer,
            QuoteLabel.Format(quote.Number),
            quote.Title,
            IssueDateOf(quote.CreatedAt),
            quote.ValidUntil,
            quote.Status == nameof(QuoteStatus.Draft),
            new QuotePdfCustomer(quote.CustomerName, string.IsNullOrWhiteSpace(customerAddress) ? null : customerAddress),
            quote.Items.OrderBy(i => i.Position).Select(i => new QuotePdfLine(i.Description, i.Quantity, i.UnitPriceCents, i.VatPercent, i.NetCents)).ToList(),
            quote.VatBreakdown.Select(b => new QuotePdfVatLine(b.VatPercent, b.BaseCents, b.VatCents)).ToList(),
            quote.NetCents,
            quote.VatCents,
            quote.TotalCents,
            quote.Notes);
    }

    /// <summary>
    /// El día de la fecha de creación <b>en hora de Madrid</b>. La base de datos guarda instantes en UTC (a veces con <c>Kind</c> sin especificar al leerlos): se tratan siempre como UTC.
    /// </summary>
    public static DateOnly IssueDateOf(DateTime createdAt)
    {
        var utc = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Madrid.Value));
    }
}

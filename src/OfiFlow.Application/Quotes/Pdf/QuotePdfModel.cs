namespace OfiFlow.Application.Quotes.Pdf;

// Lo que se imprime en el PDF de un presupuesto (spec 025 R4), ya calculado: el generador no suma, no multiplica ni redondea (R5). Todo es dato guardado en el servidor, nunca de una petición.
//
// Cada tipo de este fichero sobrescribe ToString: el `ToString` de un `record` imprime todas sus propiedades, y aquí hay el NIF y la dirección de la empresa (para un autónomo, datos personales),
// el nombre y la dirección de un cliente, texto libre del usuario e importes (ADR-009 R5). Un test de arquitectura exige que ningún tipo de este espacio de nombres se salte la regla.

/// <summary>Quien emite el documento: los datos fiscales de la empresa (spec 024). Los cinco primeros son obligatorios; el resto, opcionales.</summary>
public sealed record QuotePdfIssuer(
    string LegalName,
    string TaxId,
    string AddressLine,
    string PostalCode,
    string City,
    string? Province,
    string? Phone,
    string? Email)
{
    public override string ToString() => nameof(QuotePdfIssuer);
}

/// <summary>A quién va dirigido. Solo su nombre y su dirección: <b>nunca</b> sus notas (son internas), y por minimizar, tampoco su email ni su teléfono (spec 025 R4).</summary>
public sealed record QuotePdfCustomer(string Name, string? Address)
{
    public override string ToString() => nameof(QuotePdfCustomer);
}

/// <summary>Una línea tal y como la ve el cliente. <paramref name="NetCents"/> la calculó el servidor (spec 023 R5): el generador solo la imprime.</summary>
public sealed record QuotePdfLine(string Description, decimal Quantity, long UnitPriceCents, int VatPercent, long NetCents)
{
    public override string ToString() => nameof(QuotePdfLine);
}

/// <summary>El desglose de un tipo de IVA, calculado una vez sobre la suma de las bases de ese tipo (spec 023 R5).</summary>
public sealed record QuotePdfVatLine(int VatPercent, long BaseCents, long VatCents)
{
    public override string ToString() => nameof(QuotePdfVatLine);
}

/// <summary>
/// El documento entero. <paramref name="Label"/> es el número tal como lo ve la pantalla («P-0012»); <paramref name="IssueDate"/> es la fecha de creación del presupuesto <b>en hora de Madrid</b>
/// (spec 025 D12); <paramref name="IsDraft"/> hace que cada página lleve la marca «BORRADOR» (R2).
/// </summary>
public sealed record QuotePdfModel(
    QuotePdfIssuer Issuer,
    string Label,
    string Title,
    DateOnly IssueDate,
    DateOnly? ValidUntil,
    bool IsDraft,
    QuotePdfCustomer Customer,
    IReadOnlyList<QuotePdfLine> Lines,
    IReadOnlyList<QuotePdfVatLine> Breakdown,
    long NetCents,
    long VatCents,
    long TotalCents,
    string? Notes)
{
    public override string ToString() => nameof(QuotePdfModel);
}

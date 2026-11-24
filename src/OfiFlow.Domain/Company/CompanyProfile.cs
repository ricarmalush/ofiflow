using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Domain.Company;

/// <summary>
/// El perfil fiscal de una empresa (spec 024): quién emite los documentos (nombre fiscal, NIF y dirección). Hay <b>uno como mucho por empresa</b> (índice único
/// por <c>TenantId</c>) y nace al guardarlo por primera vez, no al registrarse (R1). Es una entidad <b>de la empresa</b> (<see cref="ITenantOwned"/>) y no columnas de
/// <c>Tenant</c>, que es la entidad global (D4).
/// <para>
/// Las invariantes viven aquí: el NIF y el código postal llegan ya como objetos de valor (no existen sin ser válidos), y un texto obligatorio vacío o pasado de su
/// límite es un error de programación (Application valida la <i>forma</i> antes y responde 400), no un código de negocio. Todo texto se guarda recortado y un
/// opcional vacío es <c>null</c> (R2). Contiene datos personales cuando la empresa es un autónomo: <b>nada de esto se registra</b> (ADR-009 R5).
/// </para>
/// </summary>
public sealed class CompanyProfile : AggregateRoot, ITenantOwned, IAuditable
{
    // Longitudes máximas: fuente única para el dominio, los validadores y EF Core (ADR-009 R3).
    public const int LegalNameMaxLength = 200;
    public const int AddressLineMaxLength = 200;
    public const int CityMaxLength = 100;
    public const int ProvinceMaxLength = 100;

    public Guid TenantId { get; private set; }

    /// <summary>El nombre o la razón social con el que se emiten los documentos. No es el nombre comercial de <c>Tenant</c> (D11).</summary>
    public string LegalName { get; private set; } = string.Empty;

    public SpanishTaxId TaxId { get; private set; } = null!;

    public string AddressLine { get; private set; } = string.Empty;

    public SpanishPostalCode PostalCode { get; private set; } = null!;

    public string City { get; private set; } = string.Empty;

    public string? Province { get; private set; }

    public PhoneNumber? Phone { get; private set; }

    public Email? Email { get; private set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private CompanyProfile()
    {
        // Requerido por EF Core.
    }

    private CompanyProfile(Guid id, Guid tenantId)
        : base(id)
    {
        TenantId = tenantId;
    }

    public static CompanyProfile Create(
        Guid tenantId,
        string legalName,
        SpanishTaxId taxId,
        string addressLine,
        SpanishPostalCode postalCode,
        string city,
        string? province,
        PhoneNumber? phone,
        Email? email)
    {
        var profile = new CompanyProfile(Guid.NewGuid(), tenantId);
        profile.Apply(legalName, taxId, addressLine, postalCode, city, province, phone, email);

        return profile;
    }

    /// <summary>Sustituye el perfil entero (R6): un opcional ausente borra el valor anterior.</summary>
    public void Update(
        string legalName,
        SpanishTaxId taxId,
        string addressLine,
        SpanishPostalCode postalCode,
        string city,
        string? province,
        PhoneNumber? phone,
        Email? email) =>
        Apply(legalName, taxId, addressLine, postalCode, city, province, phone, email);

    private void Apply(
        string legalName,
        SpanishTaxId taxId,
        string addressLine,
        SpanishPostalCode postalCode,
        string city,
        string? province,
        PhoneNumber? phone,
        Email? email)
    {
        ArgumentNullException.ThrowIfNull(taxId);
        ArgumentNullException.ThrowIfNull(postalCode);

        LegalName = Required(legalName, LegalNameMaxLength, nameof(legalName));
        TaxId = taxId;
        AddressLine = Required(addressLine, AddressLineMaxLength, nameof(addressLine));
        PostalCode = postalCode;
        City = Required(city, CityMaxLength, nameof(city));
        Province = Optional(province, ProvinceMaxLength, nameof(province));
        Phone = phone;
        Email = email;
    }

    private static string Required(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        ArgumentException.ThrowIfNullOrEmpty(trimmed, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, maxLength, name);

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, maxLength, name);

        return trimmed;
    }
}

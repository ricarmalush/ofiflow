using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Domain.Tests.Company;

/// <summary>El perfil fiscal (spec 024): lo que lo crea y lo sustituye, las cotas de cada dato y que un opcional ausente borra el anterior (R2, R6).</summary>
public class CompanyProfileTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly SpanishTaxId TaxId = SpanishTaxId.Create("12345678Z");
    private static readonly SpanishPostalCode PostalCode = SpanishPostalCode.Create("28001");

    private static CompanyProfile Create(
        string legalName = "Marta Gil Pérez",
        string addressLine = "Calle Mayor 1, 2.º B",
        string city = "Madrid",
        string? province = "Madrid",
        PhoneNumber? phone = null,
        Email? email = null) =>
        CompanyProfile.Create(TenantId, legalName, TaxId, addressLine, PostalCode, city, province, phone, email);

    private static void Update(CompanyProfile profile, string legalName = "Marta Gil Pérez", string addressLine = "Calle Mayor 1", string city = "Madrid", string? province = null, PhoneNumber? phone = null, Email? email = null) =>
        profile.Update(legalName, TaxId, addressLine, PostalCode, city, province, phone, email);

    // ---------- Crear ----------

    [Fact]
    public void Create_KeepsEveryField_AndBelongsToTheCompanyItWasCreatedFor()
    {
        var phone = PhoneNumber.Create("+34 600 123 456");
        var email = Email.Create("marta@example.com");

        var profile = Create(phone: phone, email: email);

        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal(TenantId, profile.TenantId);
        Assert.Equal("Marta Gil Pérez", profile.LegalName);
        Assert.Equal("12345678Z", profile.TaxId.Value);
        Assert.Equal("Calle Mayor 1, 2.º B", profile.AddressLine);
        Assert.Equal("28001", profile.PostalCode.Value);
        Assert.Equal("Madrid", profile.City);
        Assert.Equal("Madrid", profile.Province);
        Assert.Equal(phone, profile.Phone);
        Assert.Equal(email, profile.Email);
    }

    [Fact]
    public void EveryProfile_GetsItsOwnId()
    {
        Assert.NotEqual(Create().Id, Create().Id);
    }

    [Fact]
    public void Create_TrimsEveryText_AndAnEmptyOptionalIsNull()
    {
        var profile = Create(legalName: "  Marta Gil  ", addressLine: "  Calle Mayor 1 ", city: " Madrid  ", province: "   ");

        Assert.Equal("Marta Gil", profile.LegalName);
        Assert.Equal("Calle Mayor 1", profile.AddressLine);
        Assert.Equal("Madrid", profile.City);
        Assert.Null(profile.Province);
        Assert.Null(Create(province: null).Province);
        Assert.Null(Create(province: string.Empty).Province);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARequiredTextThatIsMissing_IsAProgrammingError_NotABusinessCode(string? missing)
    {
        // Application valida la forma antes (responde 400); si algo así llegara al dominio es un error de programación.
        Assert.ThrowsAny<ArgumentException>(() => Create(legalName: missing!));
        Assert.ThrowsAny<ArgumentException>(() => Create(addressLine: missing!));
        Assert.ThrowsAny<ArgumentException>(() => Create(city: missing!));
    }

    [Fact]
    public void EveryLimit_IsAcceptedAtTheLimit_AndRejectedOneOver()
    {
        Assert.Equal(200, CompanyProfile.LegalNameMaxLength);
        Assert.Equal(200, CompanyProfile.AddressLineMaxLength);
        Assert.Equal(100, CompanyProfile.CityMaxLength);
        Assert.Equal(100, CompanyProfile.ProvinceMaxLength);

        var atLimit = Create(
            legalName: new string('a', CompanyProfile.LegalNameMaxLength),
            addressLine: new string('b', CompanyProfile.AddressLineMaxLength),
            city: new string('c', CompanyProfile.CityMaxLength),
            province: new string('d', CompanyProfile.ProvinceMaxLength));
        Assert.Equal(CompanyProfile.LegalNameMaxLength, atLimit.LegalName.Length);
        Assert.Equal(CompanyProfile.ProvinceMaxLength, atLimit.Province!.Length);

        Assert.ThrowsAny<ArgumentException>(() => Create(legalName: new string('a', CompanyProfile.LegalNameMaxLength + 1)));
        Assert.ThrowsAny<ArgumentException>(() => Create(addressLine: new string('b', CompanyProfile.AddressLineMaxLength + 1)));
        Assert.ThrowsAny<ArgumentException>(() => Create(city: new string('c', CompanyProfile.CityMaxLength + 1)));
        Assert.ThrowsAny<ArgumentException>(() => Create(province: new string('d', CompanyProfile.ProvinceMaxLength + 1)));
    }

    [Fact]
    public void TheLimit_CountsTheTrimmedText_NotTheSpacesAroundIt()
    {
        var profile = Create(legalName: "  " + new string('a', CompanyProfile.LegalNameMaxLength) + "  ");

        Assert.Equal(CompanyProfile.LegalNameMaxLength, profile.LegalName.Length);
    }

    [Fact]
    public void ANullTaxId_OrPostalCode_IsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => CompanyProfile.Create(TenantId, "Marta", null!, "Calle", PostalCode, "Madrid", null, null, null));
        Assert.Throws<ArgumentNullException>(() => CompanyProfile.Create(TenantId, "Marta", TaxId, "Calle", null!, "Madrid", null, null, null));
    }

    // ---------- Sustituir ----------

    [Fact]
    public void Update_ReplacesEveryField()
    {
        var profile = Create(phone: PhoneNumber.Create("600123456"), email: Email.Create("a@example.com"));
        var newTaxId = SpanishTaxId.Create("A58818501");
        var newPostalCode = SpanishPostalCode.Create("08001");
        var newPhone = PhoneNumber.Create("930000000");
        var newEmail = Email.Create("b@example.com");

        profile.Update("Fontanería Marta S.L.", newTaxId, "Avenida Diagonal 5", newPostalCode, "Barcelona", "Barcelona", newPhone, newEmail);

        Assert.Equal("Fontanería Marta S.L.", profile.LegalName);
        Assert.Equal(newTaxId, profile.TaxId);
        Assert.Equal("Avenida Diagonal 5", profile.AddressLine);
        Assert.Equal(newPostalCode, profile.PostalCode);
        Assert.Equal("Barcelona", profile.City);
        Assert.Equal("Barcelona", profile.Province);
        Assert.Equal(newPhone, profile.Phone);
        Assert.Equal(newEmail, profile.Email);
    }

    [Fact]
    public void Update_WithoutAnOptional_ErasesThePreviousValue()
    {
        var profile = Create(province: "Madrid", phone: PhoneNumber.Create("600123456"), email: Email.Create("a@example.com"));

        Update(profile, province: null, phone: null, email: null);

        Assert.Null(profile.Province);
        Assert.Null(profile.Phone);
        Assert.Null(profile.Email);
    }

    [Fact]
    public void Update_KeepsTheIdAndTheCompany()
    {
        var profile = Create();
        var id = profile.Id;

        Update(profile, legalName: "Otro nombre");

        Assert.Equal(id, profile.Id);
        Assert.Equal(TenantId, profile.TenantId);
    }

    [Fact]
    public void Update_AppliesTheSameRulesAsCreate()
    {
        var profile = Create();

        Assert.ThrowsAny<ArgumentException>(() => Update(profile, legalName: "  "));
        Assert.ThrowsAny<ArgumentException>(() => Update(profile, addressLine: new string('b', CompanyProfile.AddressLineMaxLength + 1)));
        Assert.Throws<ArgumentNullException>(() => profile.Update("Marta", null!, "Calle", PostalCode, "Madrid", null, null, null));
        Assert.Equal("Marta Gil Pérez", profile.LegalName);
    }

    // ---------- Auditoría y aislamiento ----------

    [Fact]
    public void TheProfile_BelongsToACompany_AndIsAudited()
    {
        var profile = Create();

        Assert.IsAssignableFrom<ITenantOwned>(profile);
        Assert.IsAssignableFrom<IAuditable>(profile);
    }
}

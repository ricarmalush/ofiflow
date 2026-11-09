using FluentValidation.TestHelper;
using OfiFlow.Application.Company.Commands.UpsertCompanyProfile;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>
/// La forma de lo que llega al guardar el perfil fiscal (spec 024 R2 a R4): cada obligatorio, cada cota <b>en sus dos lados</b> y qué código sale de cada fallo. Las reglas del NIF y del
/// código postal son las mismas que las de los objetos de valor del dominio.
/// </summary>
public class CompanyProfileValidatorTests
{
    private readonly UpsertCompanyProfileCommandValidator _validator = new();

    private static UpsertCompanyProfileCommand Valid() =>
        new("Marta Gil Pérez", "12345678Z", "Calle Mayor 1", "28001", "Madrid", "Madrid", "600123456", "marta@example.com");

    [Fact]
    public void ACompleteProfile_HasNoErrors() => _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void TheThreeOptionals_MayBeAbsentOrBlank()
    {
        _validator.TestValidate(Valid() with { Province = null, Phone = null, Email = null }).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(Valid() with { Province = "", Phone = "", Email = "" }).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(Valid() with { Province = "  ", Phone = "  ", Email = "  " }).ShouldNotHaveAnyValidationErrors();
    }

    // ---------- Los cinco obligatorios ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EveryRequiredField_IsRequired(string? missing)
    {
        _validator.TestValidate(Valid() with { LegalName = missing! }).ShouldHaveValidationErrorFor(x => x.LegalName);
        _validator.TestValidate(Valid() with { TaxId = missing! }).ShouldHaveValidationErrorFor(x => x.TaxId);
        _validator.TestValidate(Valid() with { AddressLine = missing! }).ShouldHaveValidationErrorFor(x => x.AddressLine);
        _validator.TestValidate(Valid() with { PostalCode = missing! }).ShouldHaveValidationErrorFor(x => x.PostalCode);
        _validator.TestValidate(Valid() with { City = missing! }).ShouldHaveValidationErrorFor(x => x.City);
    }

    [Fact]
    public void AMissingTaxId_OrPostalCode_IsJustRequired_NotAlsoInvalid()
    {
        // Un único error por campo: «falta» no es lo mismo que «no es válido» (y un valor ausente, null, nunca llega a la regla de forma).
        var taxId = _validator.TestValidate(Valid() with { TaxId = null! });
        var postalCode = _validator.TestValidate(Valid() with { PostalCode = "" });

        Assert.Single(taxId.Errors, error => error.PropertyName == nameof(UpsertCompanyProfileCommand.TaxId));
        Assert.Single(postalCode.Errors, error => error.PropertyName == nameof(UpsertCompanyProfileCommand.PostalCode));
        Assert.DoesNotContain(taxId.Errors, error => error.ErrorCode == CompanyErrors.TaxIdInvalid);
        Assert.DoesNotContain(postalCode.Errors, error => error.ErrorCode == CompanyErrors.PostalCodeInvalid);
    }

    // ---------- Las cotas, en sus dos lados ----------

    [Fact]
    public void LegalName_AcceptsTheLimit_AndRejectsOneOver()
    {
        _validator.TestValidate(Valid() with { LegalName = new string('a', CompanyProfile.LegalNameMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.LegalName);
        _validator.TestValidate(Valid() with { LegalName = new string('a', CompanyProfile.LegalNameMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.LegalName);
    }

    [Fact]
    public void AddressLine_AcceptsTheLimit_AndRejectsOneOver()
    {
        _validator.TestValidate(Valid() with { AddressLine = new string('a', CompanyProfile.AddressLineMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.AddressLine);
        _validator.TestValidate(Valid() with { AddressLine = new string('a', CompanyProfile.AddressLineMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.AddressLine);
    }

    [Fact]
    public void City_AcceptsTheLimit_AndRejectsOneOver()
    {
        _validator.TestValidate(Valid() with { City = new string('a', CompanyProfile.CityMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.City);
        _validator.TestValidate(Valid() with { City = new string('a', CompanyProfile.CityMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.City);
    }

    [Fact]
    public void Province_AcceptsTheLimit_AndRejectsOneOver()
    {
        _validator.TestValidate(Valid() with { Province = new string('a', CompanyProfile.ProvinceMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.Province);
        _validator.TestValidate(Valid() with { Province = new string('a', CompanyProfile.ProvinceMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.Province);
    }

    // ---------- El NIF, NIE o CIF ----------

    [Theory]
    [InlineData("12345678Z")]
    [InlineData(" 12.345.678-z ")]
    [InlineData("X1234567L")]
    [InlineData("A58818501")]
    [InlineData("Q2826000H")]
    public void AValidTaxId_InAnyWayOfTypingIt_HasNoErrors(string taxId) =>
        _validator.TestValidate(Valid() with { TaxId = taxId }).ShouldNotHaveValidationErrorFor(x => x.TaxId);

    [Theory]
    [InlineData("12345678A")] // la letra no cuadra
    [InlineData("X1234567A")]
    [InlineData("A58818502")]
    [InlineData("12345678")]
    [InlineData("hola")]
    [InlineData("12345678Z y algo más")]
    public void AWrongTaxId_GivesCompanyTaxIdInvalid(string taxId) =>
        _validator.TestValidate(Valid() with { TaxId = taxId }).ShouldHaveValidationErrorFor(x => x.TaxId).WithErrorCode(CompanyErrors.TaxIdInvalid);

    [Fact]
    public void ATaxId_LongerThanTheInputLimit_IsRejectedByLength_BeforeItsShapeIsLookedAt()
    {
        var atLimit = "12345678Z" + new string(' ', SpanishTaxId.InputMaxLength - 9);
        var oneOver = atLimit + " ";

        _validator.TestValidate(Valid() with { TaxId = atLimit }).ShouldNotHaveValidationErrorFor(x => x.TaxId);

        var result = _validator.TestValidate(Valid() with { TaxId = oneOver });
        result.ShouldHaveValidationErrorFor(x => x.TaxId).WithErrorCode("MaximumLengthValidator");
        Assert.DoesNotContain(result.Errors, error => error.ErrorCode == CompanyErrors.TaxIdInvalid);
    }

    // ---------- El código postal ----------

    [Theory]
    [InlineData("28001")]
    [InlineData("01001")]
    [InlineData("52999")]
    [InlineData(" 28001 ")]
    public void AValidPostalCode_HasNoErrors(string code) =>
        _validator.TestValidate(Valid() with { PostalCode = code }).ShouldNotHaveValidationErrorFor(x => x.PostalCode);

    [Theory]
    [InlineData("00001")]
    [InlineData("53001")]
    [InlineData("2800")]
    [InlineData("280011")]
    [InlineData("2800A")]
    public void AWrongPostalCode_GivesCompanyPostalCodeInvalid(string code) =>
        _validator.TestValidate(Valid() with { PostalCode = code }).ShouldHaveValidationErrorFor(x => x.PostalCode).WithErrorCode(CompanyErrors.PostalCodeInvalid);

    // ---------- El teléfono y el email ----------

    [Theory]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("600 123 45x")]
    public void AWrongPhone_GivesTheSamePhoneCodeAsACustomer(string phone) =>
        _validator.TestValidate(Valid() with { Phone = phone }).ShouldHaveValidationErrorFor(x => x.Phone).WithErrorCode(CustomerErrors.PhoneInvalid);

    [Fact]
    public void APhoneLongerThanTheLimit_IsRejectedByLength_NotByShape()
    {
        var result = _validator.TestValidate(Valid() with { Phone = "+34 600 123 456 789 000 111" });

        result.ShouldHaveValidationErrorFor(x => x.Phone).WithErrorCode("MaximumLengthValidator");
        Assert.DoesNotContain(result.Errors, error => error.ErrorCode == CustomerErrors.PhoneInvalid);
    }

    [Fact]
    public void APhone_IsCheckedTrimmed_BecauseThatIsWhatIsStored_ButItsLengthIsNot()
    {
        _validator.TestValidate(Valid() with { Phone = "  600123456  " }).ShouldNotHaveValidationErrorFor(x => x.Phone);

        // 21 caracteres con espacios: pasa del límite aunque recortado cupiera.
        _validator.TestValidate(Valid() with { Phone = " " + new string('6', 19) + " " }).ShouldHaveValidationErrorFor(x => x.Phone);
    }

    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    [InlineData("dos@@example.com")]
    public void AWrongEmail_GivesTheSameEmailCodeAsACustomer(string email) =>
        _validator.TestValidate(Valid() with { Email = email }).ShouldHaveValidationErrorFor(x => x.Email).WithErrorCode(CommonErrors.EmailInvalid);

    [Fact]
    public void AnEmail_IsCheckedTrimmed_AndAnEnormousOneIsRejectedByLength()
    {
        _validator.TestValidate(Valid() with { Email = "  marta@example.com  " }).ShouldNotHaveValidationErrorFor(x => x.Email);

        var tooLong = new string('a', Email.MaxLength) + "@example.com";
        var result = _validator.TestValidate(Valid() with { Email = tooLong });
        result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorCode("MaximumLengthValidator");
        Assert.DoesNotContain(result.Errors, error => error.ErrorCode == CommonErrors.EmailInvalid);
    }

    // ---------- Lo que se rechaza se rechaza con el mismo criterio que el dominio ----------

    [Fact]
    public void WhateverTheValidatorAccepts_TheValueObjectsAcceptToo()
    {
        foreach (var taxId in new[] { "12345678Z", " 12.345.678-z ", "x-1234567-l", "A58818501", "Q2826000H" })
        {
            Assert.True(_validator.TestValidate(Valid() with { TaxId = taxId }).IsValid);
            Assert.Equal(SpanishTaxId.Normalize(taxId), SpanishTaxId.Create(taxId).Value);
        }

        foreach (var code in new[] { "28001", " 28001 " })
        {
            Assert.True(_validator.TestValidate(Valid() with { PostalCode = code }).IsValid);
            Assert.Equal(code.Trim(), SpanishPostalCode.Create(code).Value);
        }
    }
}

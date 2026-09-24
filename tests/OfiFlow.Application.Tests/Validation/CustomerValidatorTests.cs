using FluentValidation.TestHelper;
using OfiFlow.Application.Customers.Commands.CreateCustomer;
using OfiFlow.Application.Customers.Commands.UpdateCustomer;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>ADR-009 R3: cada campo de texto acepta su longitud máxima exacta y rechaza una más.</summary>
public class CustomerValidatorTests
{
    private readonly CreateCustomerCommandValidator _create = new();
    private readonly UpdateCustomerCommandValidator _update = new();

    private static CreateCustomerCommand ValidCreate() =>
        new(CustomerType.Person, "Juan Pérez", "juan@example.com", "+34 600 123 456", "Calle Mayor 1", "Notas");

    [Fact]
    public void ValidCommand_HasNoErrors()
    {
        _create.TestValidate(ValidCreate()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Name_AtMaxLength_IsValid_AndOneMore_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Name = new string('a', Customer.NameMaxLength) })
            .ShouldNotHaveValidationErrorFor(x => x.Name);

        _create.TestValidate(ValidCreate() with { Name = new string('a', Customer.NameMaxLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Address_AtMaxLength_IsValid_AndOneMore_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Address = new string('a', Customer.AddressMaxLength) })
            .ShouldNotHaveValidationErrorFor(x => x.Address);

        _create.TestValidate(ValidCreate() with { Address = new string('a', Customer.AddressMaxLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Address);
    }

    [Fact]
    public void Notes_AtMaxLength_IsValid_AndOneMore_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Notes = new string('a', Customer.NotesMaxLength) })
            .ShouldNotHaveValidationErrorFor(x => x.Notes);

        _create.TestValidate(ValidCreate() with { Notes = new string('a', Customer.NotesMaxLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Notes);
    }

    [Fact]
    public void Email_ThatDomainWouldReject_IsInvalid_InsteadOfFailingLaterWith500()
    {
        // "a@b" pasaba el EmailAddress() de FluentValidation pero no la regla de Domain.
        _create.TestValidate(ValidCreate() with { Email = "a@b" })
            .ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Email_TooLong_ReportsASingleError()
    {
        var tooLong = new string('a', Email.MaxLength) + "@example.com";

        var result = _create.TestValidate(ValidCreate() with { Email = tooLong });

        Assert.Single(result.Errors, e => e.PropertyName == nameof(CreateCustomerCommand.Email));
    }

    [Fact]
    public void Phone_ThatDomainWouldReject_IsInvalid_InsteadOfFailingLaterWith500()
    {
        _create.TestValidate(ValidCreate() with { Phone = "abc" })
            .ShouldHaveValidationErrorFor(x => x.Phone);
    }

    [Fact]
    public void ErrorMessages_DoNotContainPersonalData()
    {
        const string email = "juan.perez@sin-dominio";
        const string phone = "600-abc-123";

        var result = _create.TestValidate(ValidCreate() with { Email = email, Phone = phone });

        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, e =>
        {
            Assert.DoesNotContain(email, e.ErrorMessage);
            Assert.DoesNotContain(phone, e.ErrorMessage);
        });
    }

    [Fact]
    public void Type_OutOfEnumRange_IsInvalid()
    {
        _create.TestValidate(ValidCreate() with { Type = (CustomerType)999 })
            .ShouldHaveValidationErrorFor(x => x.Type);
    }

    [Fact]
    public void Update_AppliesTheSameLimits()
    {
        var command = new UpdateCustomerCommand(
            Guid.NewGuid(),
            new string('a', Customer.NameMaxLength + 1),
            "a@b",
            "abc",
            new string('a', Customer.AddressMaxLength + 1),
            new string('a', Customer.NotesMaxLength + 1));

        var result = _update.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.Phone);
        result.ShouldHaveValidationErrorFor(x => x.Address);
        result.ShouldHaveValidationErrorFor(x => x.Notes);
    }
}

using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Domain.Tests.Customers;

public class CustomerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidName_Succeeds()
    {
        var customer = Customer.Create(TenantId, CustomerType.Person, "Juan Pérez", null, null, null, null);

        Assert.Equal(TenantId, customer.TenantId);
        Assert.Equal("Juan Pérez", customer.Name);
        Assert.Equal(CustomerType.Person, customer.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            Customer.Create(TenantId, CustomerType.Person, name, null, null, null, null));
    }

    [Fact]
    public void Create_WithInvalidEmail_Throws()
    {
        Assert.Throws<ArgumentException>(() => Email.Create("no-es-un-email"));
    }

    [Fact]
    public void Create_WithInvalidPhone_Throws()
    {
        Assert.Throws<ArgumentException>(() => PhoneNumber.Create("abc"));
    }

    [Fact]
    public void UpdateContactInfo_ChangesContactFields()
    {
        var customer = Customer.Create(TenantId, CustomerType.Company, "Ferretería Pérez", null, null, null, null);
        var email = Email.Create("contacto@ferreteria.com");

        customer.UpdateContactInfo("Ferretería Pérez SL", email, null, "Calle Mayor 1", "Cliente preferente");

        Assert.Equal("Ferretería Pérez SL", customer.Name);
        Assert.Equal(email, customer.Email);
        Assert.Equal("Calle Mayor 1", customer.Address);
        Assert.Equal("Cliente preferente", customer.Notes);
    }

    [Fact]
    public void UpdateContactInfo_WithoutName_Throws()
    {
        var customer = Customer.Create(TenantId, CustomerType.Person, "Juan Pérez", null, null, null, null);

        Assert.Throws<ArgumentException>(() =>
            customer.UpdateContactInfo("", null, null, null, null));
    }
}

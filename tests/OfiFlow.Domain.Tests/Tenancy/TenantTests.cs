using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

public class TenantTests
{
    [Fact]
    public void Create_WithValidName_Succeeds()
    {
        var tenant = Tenant.Create("Fontanería Pérez");

        Assert.Equal("Fontanería Pérez", tenant.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => Tenant.Create(name));
    }
}

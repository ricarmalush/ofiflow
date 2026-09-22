using OfiFlow.Domain.Customers;

namespace OfiFlow.Domain.Tests.Customers;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+34 600 123 456")]
    [InlineData("912345678")]
    public void Create_WithValidFormat_Succeeds(string value)
    {
        var phone = PhoneNumber.Create(value);

        Assert.Equal(value, phone.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void Create_WithInvalidFormat_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => PhoneNumber.Create(value));
    }
}

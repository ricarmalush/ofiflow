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

    [Fact]
    public void Create_WithInvalidFormat_DoesNotLeakValueInMessage()
    {
        const string personalData = "600-abc-123";

        var exception = Assert.Throws<ArgumentException>(() => PhoneNumber.Create(personalData));

        Assert.DoesNotContain(personalData, exception.Message);
    }

    [Theory]
    [InlineData("+34 600 123 456", true)]
    [InlineData("12345", false)]
    [InlineData("123456789012345678901", false)]
    [InlineData(null, false)]
    public void IsValid_MatchesCreateRules(string? value, bool expected)
    {
        Assert.Equal(expected, PhoneNumber.IsValid(value));
    }
}

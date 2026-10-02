using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Tests.Common;

public class EmailTests
{
    [Theory]
    [InlineData("juan@example.com")]
    [InlineData("maria.garcia@ofiflow.es")]
    public void Create_WithValidFormat_Succeeds(string value)
    {
        var email = Email.Create(value);

        Assert.Equal(value, email.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-es-un-email")]
    [InlineData("falta-arroba.com")]
    public void Create_WithInvalidFormat_Throws(string value)
    {
        Assert.Throws<DomainException>(() => Email.Create(value));
    }

    [Fact]
    public void Create_WithInvalidFormat_DoesNotLeakValueInMessage()
    {
        const string personalData = "juan.perez@sin-dominio";

        var exception = Assert.Throws<DomainException>(() => Email.Create(personalData));

        Assert.Equal(CommonErrors.EmailInvalid, exception.Code);

        Assert.DoesNotContain(personalData, exception.Message);
    }

    [Fact]
    public void IsValid_WhenLongerThanMaxLength_ReturnsFalse()
    {
        var tooLong = new string('a', Email.MaxLength - "@example.com".Length + 1) + "@example.com";

        Assert.False(Email.IsValid(tooLong));
    }

    [Theory]
    [InlineData("juan@example.com", true)]
    [InlineData("a@b", false)]
    [InlineData(null, false)]
    public void IsValid_MatchesCreateRules(string? value, bool expected)
    {
        Assert.Equal(expected, Email.IsValid(value));
    }
}

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
        Assert.Throws<ArgumentException>(() => Email.Create(value));
    }
}

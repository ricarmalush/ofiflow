using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Domain.Tests.Identity;

public class UserTests
{
    [Fact]
    public void Create_WithValidName_Succeeds()
    {
        var id = Guid.NewGuid();
        var email = Email.Create("juan@example.com");

        var user = User.Create(id, "Juan Pérez", email);

        Assert.Equal(id, user.Id);
        Assert.Equal("Juan Pérez", user.Name);
        Assert.Equal(email, user.ContactEmail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutName_Throws(string name)
    {
        var email = Email.Create("juan@example.com");

        var exception = Assert.Throws<DomainException>(() => User.Create(Guid.NewGuid(), name, email));

        Assert.Equal(IdentityErrors.UserNameRequired, exception.Code);
    }
}

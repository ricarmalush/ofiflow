using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Domain.Tests.Identity;

/// <summary>Las condiciones de uso aceptadas por una persona (spec 021 R16, R19): solo la etiqueta de la versión y la fecha, nunca el texto.</summary>
public class UserTermsTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private static User NewUser() => User.Create(Guid.NewGuid(), "Ana", Email.Create("ana@example.com"));

    [Fact]
    public void ANewUser_HasAcceptedNothing_TheExistingAccountsAreNotTouched()
    {
        var user = NewUser();

        Assert.Null(user.TermsVersion);
        Assert.Null(user.TermsAcceptedAt);
    }

    [Fact]
    public void AcceptTerms_RecordsTheVersionAndTheMoment()
    {
        var user = NewUser();

        user.AcceptTerms("2026-11", Now);

        Assert.Equal("2026-11", user.TermsVersion);
        Assert.Equal(Now, user.TermsAcceptedAt);
    }

    [Fact]
    public void AcceptingANewerVersion_ReplacesTheOldOne()
    {
        var user = NewUser();
        user.AcceptTerms("2026-11", Now);

        user.AcceptTerms("2027-02", Now.AddMonths(3));

        Assert.Equal("2027-02", user.TermsVersion);
        Assert.Equal(Now.AddMonths(3), user.TermsAcceptedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AcceptTerms_RefusesAnEmptyVersion_AndChangesNothing(string version)
    {
        var user = NewUser();

        Assert.Throws<ArgumentException>(() => user.AcceptTerms(version, Now));

        Assert.Null(user.TermsVersion);
        Assert.Null(user.TermsAcceptedAt);
    }

    [Fact]
    public void AcceptTerms_AcceptsAVersionAsLongAsTheColumn_AndRefusesOneMore()
    {
        var user = NewUser();
        var version = new string('v', User.TermsVersionMaxLength);

        user.AcceptTerms(version, Now);

        Assert.Equal(version, user.TermsVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => user.AcceptTerms(version + "x", Now));
        Assert.Equal(version, user.TermsVersion); // la que no cabe no pisa la buena
    }

    [Fact]
    public void TheVersionColumn_IsAShortLabel_NotAText()
    {
        Assert.Equal(20, User.TermsVersionMaxLength);
    }
}

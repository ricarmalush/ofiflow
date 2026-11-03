using Microsoft.Extensions.Options;
using OfiFlow.Infrastructure.Mail;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// Los enlaces de los correos (spec 017, R3, D6): se construyen solo con la configuración del despliegue y llevan el token en el fragmento.
/// </summary>
public class EmailLinksTests
{
    private static EmailLinks For(string? baseUrl) => new(Options.Create(new EmailOptions { LinkBaseUrl = baseUrl }));

    [Theory]
    [InlineData("https://app.ofiflow.example")]
    [InlineData("https://app.ofiflow.example:8443")]
    [InlineData("http://localhost:4200")]
    public void TheInvitationLink_StartsWithTheConfiguredBase_AndKeepsTheTokenInTheFragment(string baseUrl)
    {
        var link = For(baseUrl).AcceptInvitation("abc-DEF_123");

        Assert.Equal($"{baseUrl}/auth/accept-invitation#token=abc-DEF_123", link);
    }

    [Fact]
    public void TheTokenGoesAfterTheHash_NeverInTheQuery()
    {
        // El navegador no envía el fragmento al servidor: no acaba en logs de servidores ni de proxies ni en la cabecera Referer.
        var link = For("https://app.ofiflow.example").AcceptInvitation("tok");

        var hash = link.IndexOf('#', StringComparison.Ordinal);
        Assert.True(hash > 0);
        Assert.DoesNotContain('?', link);
        Assert.True(link.IndexOf("tok", StringComparison.Ordinal) > hash);
    }

    [Fact]
    public void ATokenWithUnexpectedCharacters_IsEncoded_SoItCannotBreakOutOfTheLink()
    {
        var link = For("https://app.ofiflow.example").AcceptInvitation("a b&c=d#e?f/../g");

        Assert.Equal("https://app.ofiflow.example/auth/accept-invitation#token=a%20b%26c%3Dd%23e%3Ff%2F..%2Fg", link);
        Assert.Single(link, '#');
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAConfiguredBase_ItFailsLoudly_InsteadOfBuildingALinkToNowhere(string? baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => For(baseUrl).AcceptInvitation("tok"));
    }

    // ===== El enlace de restablecer la contraseña (spec 018 R5) =====

    [Theory]
    [InlineData("https://app.ofiflow.example")]
    [InlineData("https://app.ofiflow.example:8443")]
    [InlineData("http://localhost:4200")]
    public void TheResetLink_StartsWithTheConfiguredBase_AndKeepsTheTokenInTheFragment(string baseUrl)
    {
        Assert.Equal($"{baseUrl}/auth/reset-password#token=abc-DEF_123", For(baseUrl).ResetPassword("abc-DEF_123"));
    }

    [Fact]
    public void TheResetLink_IsADifferentPage_ThanTheInvitationLink()
    {
        var links = For("https://app.ofiflow.example");

        Assert.NotEqual(links.AcceptInvitation("tok"), links.ResetPassword("tok"));
        Assert.Contains("/auth/reset-password#", links.ResetPassword("tok"));
        Assert.DoesNotContain("accept-invitation", links.ResetPassword("tok"));
    }

    [Fact]
    public void TheResetToken_GoesAfterTheHash_NeverInTheQuery_AndIsEncoded()
    {
        var link = For("https://app.ofiflow.example").ResetPassword("a b&c=d#e?f/../g");

        Assert.Equal("https://app.ofiflow.example/auth/reset-password#token=a%20b%26c%3Dd%23e%3Ff%2F..%2Fg", link);
        Assert.Single(link, '#');
        Assert.DoesNotContain('?', link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAConfiguredBase_TheResetLinkFailsLoudly(string? baseUrl)
    {
        Assert.Throws<InvalidOperationException>(() => For(baseUrl).ResetPassword("tok"));
    }
}

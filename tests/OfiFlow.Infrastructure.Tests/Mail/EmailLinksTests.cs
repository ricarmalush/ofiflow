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
}

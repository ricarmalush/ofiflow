using OfiFlow.Application.Identity.Mfa;

namespace OfiFlow.Application.Tests.Identity.Mfa;

public class TotpSecretTests
{
    [Fact]
    public void Generate_Returns20Bytes_AndADifferentSecretEveryTime()
    {
        var secrets = Enumerable.Range(0, 1000).Select(_ => Convert.ToBase64String(TotpSecret.Generate())).ToList();

        Assert.Equal(20, TotpSecret.ByteLength);
        Assert.All(secrets, secret => Assert.Equal(20, Convert.FromBase64String(secret).Length));
        Assert.Equal(secrets.Count, secrets.Distinct().Count()); // 160 bits: ni una repetición en mil
    }

    [Fact]
    public void Generate_DoesNotReturnAllZeroBytes()
    {
        // Control de un generador roto que devolviera un buffer sin rellenar.
        Assert.Contains(TotpSecret.Generate(), value => value != 0);
    }

    [Fact]
    public void BuildUri_HasEveryParameterTheAuthenticatorAppsNeed()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        var uri = TotpSecret.BuildUri("OfiFlow", "ana@example.com", secret);

        Assert.Equal(
            $"otpauth://totp/OfiFlow:ana%40example.com?secret={Base32.Encode(secret)}&issuer=OfiFlow&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    [Fact]
    public void BuildUri_EscapesTheIssuerAndTheAccount_SoTheyCannotBreakTheUri()
    {
        var uri = TotpSecret.BuildUri("Ofi Flow & Co", "a&secret=EVIL@example.com", new byte[20]);

        // Ni el '&' ni el '=' ni el espacio llegan sin escapar: no puede inyectarse un segundo "secret=".
        Assert.Equal(1, CountOccurrences(uri, "secret="));
        Assert.Contains("Ofi%20Flow%20%26%20Co", uri);
        Assert.Contains("a%26secret%3DEVIL%40example.com", uri);
        Assert.DoesNotContain(' ', uri);
    }

    [Theory]
    [InlineData("", "ana@example.com")]
    [InlineData("  ", "ana@example.com")]
    [InlineData("OfiFlow", "")]
    [InlineData("OfiFlow", " ")]
    public void BuildUri_RejectsAnEmptyIssuerOrAccount(string issuer, string account)
    {
        Assert.ThrowsAny<ArgumentException>(() => TotpSecret.BuildUri(issuer, account, new byte[20]));
    }

    private static int CountOccurrences(string text, string fragment)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }
}

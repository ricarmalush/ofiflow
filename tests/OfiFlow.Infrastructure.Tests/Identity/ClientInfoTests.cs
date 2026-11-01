using System.Net;
using Microsoft.AspNetCore.Http;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Lo que se guarda del cliente al iniciar una sesión (spec 016, R7 y R9, ADR-019): la IP <b>truncada</b> y el navegador saneado. Es una decisión de
/// privacidad, así que cada caso de «se trunca» va con lo que NO debe quedar: la IP completa no puede llegar nunca a ningún sitio.
/// </summary>
public class ClientInfoTests
{
    // ===== La IP truncada =====

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0")]
    [InlineData("192.168.1.255", "192.168.1.0")]
    [InlineData("10.20.30.40", "10.20.30.0")]
    [InlineData("8.8.8.8", "8.8.8.0")]
    public void TruncateIp_Ipv4_DropsTheLastOctet(string address, string expected) =>
        Assert.Equal(expected, ClientInfo.TruncateIp(IPAddress.Parse(address)));

    [Theory]
    [InlineData("2001:db8:abcd:1234:5678:9abc:def0:1234", "2001:db8:abcd::")]
    [InlineData("2001:db8:abcd::1", "2001:db8:abcd::")]
    [InlineData("2a02:9000:ffff:ffff:ffff:ffff:ffff:ffff", "2a02:9000:ffff::")]
    public void TruncateIp_Ipv6_KeepsOnlyTheFirst48Bits(string address, string expected) =>
        Assert.Equal(expected, ClientInfo.TruncateIp(IPAddress.Parse(address)));

    [Fact]
    public void TruncateIp_Ipv4MappedInIpv6_IsTreatedAsIpv4()
    {
        // Un servidor de doble pila ve una IPv4 como ::ffff:a.b.c.d. Sin tratarla aparte, se conservarían los 48 bits de «::ffff:2033» y se perdería el octeto equivocado.
        Assert.Equal("203.0.113.0", ClientInfo.TruncateIp(IPAddress.Parse("::ffff:203.0.113.77")));
    }

    [Fact]
    public void TruncateIp_AZoneIdentifier_DoesNotSurvive()
    {
        Assert.Equal("fe80::", ClientInfo.TruncateIp(IPAddress.Parse("fe80::1ff:fe23:4567:890a%3")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void TruncateIp_Loopback_IsKeptAsIs_BecauseItIsTheMachineItselfAndNotAPerson(string address) =>
        Assert.Equal(address, ClientInfo.TruncateIp(IPAddress.Parse(address)));

    [Fact]
    public void TruncateIp_WithoutAnAddress_IsNull() => Assert.Null(ClientInfo.TruncateIp(null));

    [Fact]
    public void TruncateIp_NeverLeavesTheFullAddress_ForAnyIpv4()
    {
        // La garantía de R9, comprobada con muchas direcciones: el resultado nunca es la dirección completa, y solo cambia el último octeto.
        var random = new Random(16);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[4];
            random.NextBytes(bytes);
            bytes[3] = (byte)random.Next(1, 256); // un último octeto que no sea 0, para que se note
            var address = new IPAddress(bytes);
            if (IPAddress.IsLoopback(address))
            {
                continue;
            }

            var truncated = ClientInfo.TruncateIp(address)!;
            Assert.NotEqual(address.ToString(), truncated);
            Assert.EndsWith(".0", truncated);
            Assert.Equal(string.Join('.', bytes[..3]), truncated[..^2]);
        }
    }

    [Fact]
    public void TruncateIp_NeverLeavesMoreThanThe48Bits_ForAnyIpv6()
    {
        var random = new Random(17);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            bytes[0] = 0x20; // evitar ::1, ::ffff:… y similares: una IPv6 global normal
            var address = new IPAddress(bytes);

            var parsedBack = IPAddress.Parse(ClientInfo.TruncateIp(address)!).GetAddressBytes();

            Assert.Equal(bytes[..6], parsedBack[..6]);
            Assert.All(parsedBack[6..], b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void TheLongestPossibleTruncatedIp_FitsTheColumn()
    {
        // La columna mide MaxIpLength: lo más largo que puede salir del truncado (una IPv6 de 48 bits) cabe de sobra.
        var longest = ClientInfo.TruncateIp(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"))!;

        Assert.True(longest.Length <= ClientInfo.MaxIpLength);
    }

    // ===== El navegador saneado =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("\u0001\u0002\u0003")]
    public void CleanUserAgent_WithNothingUseful_IsNull(string? raw) => Assert.Null(ClientInfo.CleanUserAgent(raw));

    [Fact]
    public void CleanUserAgent_AnOrdinaryBrowser_IsKeptAsIs()
    {
        const string chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        Assert.Equal(chrome, ClientInfo.CleanUserAgent(chrome));
    }

    [Fact]
    public void CleanUserAgent_RemovesControlCharacters_SoALineBreakCannotSlipIntoAnythingThatShowsIt()
    {
        var cleaned = ClientInfo.CleanUserAgent("Mozilla/5.0\r\nX-Injected: 1\u0000\tEnd");

        Assert.Equal("Mozilla/5.0X-Injected: 1End", cleaned);
        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\0', cleaned);
    }

    [Fact]
    public void CleanUserAgent_TrimsTheEnds_AndKeepsAccents()
    {
        Assert.Equal("Navegador de prueba ñ á", ClientInfo.CleanUserAgent("   Navegador de prueba ñ á  "));
    }

    [Fact]
    public void CleanUserAgent_IsCutToTheColumnSize_NoMoreNoLess()
    {
        Assert.Equal(256, ClientInfo.MaxUserAgentLength);
        Assert.Equal(256, ClientInfo.CleanUserAgent(new string('a', 256))!.Length);   // justo en el tope: intacto
        Assert.Equal(256, ClientInfo.CleanUserAgent(new string('a', 257))!.Length);   // uno más: recortado
        Assert.Equal(256, ClientInfo.CleanUserAgent(new string('a', 100_000))!.Length); // enorme
    }

    [Fact]
    public void CleanUserAgent_NeverCutsASurrogatePairInHalf()
    {
        // 255 caracteres y un emoji (dos unidades UTF-16): cortar en 256 dejaría la mitad de una pareja, que no es texto válido.
        var cleaned = ClientInfo.CleanUserAgent(new string('a', 255) + "😀")!;

        Assert.Equal(255, cleaned.Length);
        Assert.False(char.IsHighSurrogate(cleaned[^1]));
    }

    // ===== Lo que ve de la petición =====

    private static ClientInfo ForRequest(IPAddress? remote, string? userAgent)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote;
        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        return new ClientInfo(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void FromTheRequest_ReadsTheTruncatedIpAndTheBrowser()
    {
        var info = ForRequest(IPAddress.Parse("203.0.113.77"), "Chrome/120");

        Assert.Equal("203.0.113.0", info.IpAddress);
        Assert.Equal("Chrome/120", info.UserAgent);
    }

    [Fact]
    public void FromTheRequest_WithoutTheHeader_OrWithoutAnAddress_GivesNulls()
    {
        var info = ForRequest(remote: null, userAgent: null);

        Assert.Null(info.IpAddress);
        Assert.Null(info.UserAgent);
    }

    [Fact]
    public void WithoutAnHttpContext_BothAreNull_NotAnException()
    {
        // Por ejemplo, un proceso que emite una sesión fuera de una petición (los tests, una herramienta).
        var info = new ClientInfo(new HttpContextAccessor());

        Assert.Null(info.IpAddress);
        Assert.Null(info.UserAgent);
    }

    [Fact]
    public void FromTheRequest_TheFullAddress_NeverAppearsInAnyOfTheTwoValues()
    {
        var info = ForRequest(IPAddress.Parse("198.51.100.200"), "Firefox/121");

        Assert.DoesNotContain("198.51.100.200", $"{info.IpAddress}|{info.UserAgent}");
    }
}

using OfiFlow.Application.Identity;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// La etiqueta del dispositivo de una sesión (spec 016, D10): «Chrome en Windows». El <c>User-Agent</c> lo controla el cliente, así que lo más importante no
/// es acertar con cada navegador sino que <b>el resultado sea siempre una etiqueta de un conjunto cerrado</b> y nunca el texto que mandó (un atacante
/// podría mandar HTML). Por eso hay una tabla de navegadores reales y, aparte, una prueba con miles de cadenas aleatorias y hostiles.
/// </summary>
public class DeviceLabelTests
{
    [Theory]
    // Chrome en los cinco sistemas
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", "Chrome en Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", "Chrome en macOS")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", "Chrome en Linux")]
    [InlineData("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", "Chrome en ChromeOS")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36", "Chrome en Android")]
    // Los que llevan «Chrome/» o «Safari/» dentro de su texto y deben reconocerse antes
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0", "Edge en Windows")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36 EdgA/120.0.0.0", "Edge en Android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 OPR/106.0.0.0", "Opera en Windows")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SAMSUNG SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/23.0 Chrome/115.0.0.0 Mobile Safari/537.36", "Samsung Internet en Android")]
    // Firefox
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0", "Firefox en Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0", "Firefox en Linux")]
    [InlineData("Mozilla/5.0 (Android 13; Mobile; rv:121.0) Gecko/121.0 Firefox/121.0", "Firefox en Android")]
    // Safari y los navegadores de iOS (que llevan «like Mac OS X» y deben ser iPhone o iPad, no macOS)
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15", "Safari en macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1", "Safari en iPhone")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1", "Safari en iPad")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/120.0.6099.119 Mobile/15E148 Safari/604.1", "Chrome en iPhone")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/121.0 Mobile/15E148 Safari/605.1.15", "Firefox en iPhone")]
    public void From_RecognizesTheRealBrowsers(string userAgent, string expected) => Assert.Equal(expected, DeviceLabel.From(userAgent));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curl/8.4.0")]
    [InlineData("PostmanRuntime/7.36.0")]
    [InlineData("python-requests/2.31.0")]
    [InlineData("okhttp/4.12.0")]
    [InlineData("Dalvik/2.1.0 (Linux; U; Android 13; Pixel 7 Build/TQ3A.230901.001)")] // una app de Android, no un navegador
    [InlineData("Safari/605.1.15")]                                                       // «Safari/» copiado sin «Version/»: no es Safari
    [InlineData("<script>alert(1)</script>")]
    [InlineData("'; DROP TABLE RefreshTokens; --")]
    [InlineData("Windows")]                                                               // un sistema sin navegador no es un dispositivo reconocible
    public void From_AnythingThatIsNotABrowser_IsUnknown(string? userAgent) => Assert.Equal("Dispositivo desconocido", DeviceLabel.From(userAgent));

    [Fact]
    public void From_ABrowserWithoutAKnownSystem_ShowsOnlyTheBrowser() =>
        Assert.Equal("Chrome", DeviceLabel.From("Mozilla/5.0 (Plan9; Custom) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36"));

    [Fact]
    public void From_IsNotCaseSensitive() => Assert.Equal("Firefox en Windows", DeviceLabel.From("mozilla/5.0 (WINDOWS nt 10.0) gecko/20100101 FIREFOX/121.0"));

    [Fact]
    public void From_NeverReflectsTheClientsText_EvenWhenItContainsAKnownBrowser()
    {
        // Un navegador conocido mezclado con HTML: la etiqueta es «Chrome en Windows» y nada de lo demás se cuela.
        var label = DeviceLabel.From("<img src=x onerror=alert(1)> Windows Chrome/120 <script>");

        Assert.Equal("Chrome en Windows", label);
        Assert.DoesNotContain("<", label);
    }

    private static readonly string[] Browsers = ["Edge", "Opera", "Samsung Internet", "Firefox", "Chrome", "Safari"];
    private static readonly string[] Systems = ["iPhone", "iPad", "Android", "Windows", "ChromeOS", "macOS", "Linux"];

    private static HashSet<string> ClosedSet() =>
    [
        DeviceLabel.Unknown,
        .. Browsers,
        .. Browsers.SelectMany(browser => Systems.Select(system => $"{browser} en {system}"))
    ];

    [Fact]
    public void From_ForThousandsOfRandomAndHostileStrings_AlwaysReturnsALabelOfTheClosedSet()
    {
        // La garantía que importa (D10): lo que llega a la pantalla es una etiqueta fija, nunca algo que escribió el cliente.
        var closed = ClosedSet();
        var random = new Random(160);
        string[] fragments = ["Chrome/", "Edg/", "Firefox/", "Safari/", "Version/", "Windows", "Android", "iPhone", "Linux", "Mac OS X", "<script>", "\"><img", "' OR 1=1 --", "{{7*7}}", "%00", "\r\n", "ñ", "😀", "OPR/", "CriOS/"];

        for (var i = 0; i < 3000; i++)
        {
            var pieces = Enumerable.Range(0, random.Next(0, 8)).Select(_ => random.Next(3) == 0
                ? new string([.. Enumerable.Range(0, random.Next(1, 30)).Select(_ => (char)random.Next(32, 0x2FF))])
                : fragments[random.Next(fragments.Length)]);
            var userAgent = string.Concat(pieces);

            var label = DeviceLabel.From(userAgent);

            Assert.True(closed.Contains(label), $"«{label}» no es una etiqueta del conjunto cerrado (entrada: «{userAgent}»)");
        }
    }

    [Fact]
    public void From_AnEnormousUserAgent_IsStillAnswered()
    {
        Assert.Equal("Chrome en Windows", DeviceLabel.From(new string('x', 200_000) + " Windows Chrome/120"));
    }
}

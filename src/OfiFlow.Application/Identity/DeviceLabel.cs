namespace OfiFlow.Application.Identity;

/// <summary>
/// Convierte el <c>User-Agent</c> de una sesión en una etiqueta legible («Chrome en Windows») para la lista de sesiones (spec 016, D10). Es un parser
/// <b>pequeño y propio</b>, sin dependencias, que solo reconoce familias conocidas de navegador y de sistema.
/// </summary>
/// <remarks>
/// <b>Nunca refleja el texto del cliente</b>: el resultado es siempre una de las cadenas fijas de este fichero. El <c>User-Agent</c> lo controla quien hace la
/// petición (un atacante puede mandar HTML o lo que quiera); al convertirlo en una etiqueta de un conjunto cerrado, lo que llega a la pantalla no puede
/// ser nada de lo que escribió. Ante cualquier otra cosa (un cliente de línea de comandos, una aplicación, un texto raro), «Dispositivo desconocido».
/// </remarks>
public static class DeviceLabel
{
    public const string Unknown = "Dispositivo desconocido";

    public static string From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Unknown;
        }

        var browser = Browser(userAgent);
        if (browser is null)
        {
            return Unknown;
        }

        var system = System(userAgent);

        return system is null ? browser : $"{browser} en {system}";
    }

    // El ORDEN importa: Edge, Opera, Samsung y Chrome en iOS llevan «Chrome/» o «Safari/» en su texto, así que se reconocen antes que Chrome y Safari.
    private static string? Browser(string ua)
    {
        if (Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/") || Has(ua, "Edge/"))
        {
            return "Edge";
        }

        if (Has(ua, "OPR/") || Has(ua, "OPiOS/") || Has(ua, "Opera"))
        {
            return "Opera";
        }

        if (Has(ua, "SamsungBrowser/"))
        {
            return "Samsung Internet";
        }

        if (Has(ua, "Firefox/") || Has(ua, "FxiOS/"))
        {
            return "Firefox";
        }

        if (Has(ua, "Chrome/") || Has(ua, "CriOS/") || Has(ua, "Chromium/"))
        {
            return "Chrome";
        }

        // Safari de verdad lleva «Version/»; muchos clientes que no son Safari solo copian «Safari/» al final de su texto.
        return Has(ua, "Safari/") && Has(ua, "Version/") ? "Safari" : null;
    }

    // iPhone y iPad llevan «like Mac OS X» en su texto, así que van antes que macOS; Android lleva «Linux», así que va antes que Linux.
    private static string? System(string ua)
    {
        if (Has(ua, "iPhone"))
        {
            return "iPhone";
        }

        if (Has(ua, "iPad"))
        {
            return "iPad";
        }

        if (Has(ua, "Android"))
        {
            return "Android";
        }

        if (Has(ua, "Windows"))
        {
            return "Windows";
        }

        if (Has(ua, "CrOS"))
        {
            return "ChromeOS";
        }

        if (Has(ua, "Macintosh") || Has(ua, "Mac OS X"))
        {
            return "macOS";
        }

        return Has(ua, "Linux") || Has(ua, "X11") ? "Linux" : null;
    }

    private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace OfiFlow.Api.Common;

/// <summary>
/// Idioma de las respuestas (ADR-011 R3, sección 34 del prompt maestro).
/// Lista cerrada de idiomas: la cabecera Accept-Language solo puede elegir entre ellos y
/// cualquier otro valor cae al idioma por defecto. Así la API responde igual en cualquier
/// servidor, tenga el sistema operativo en el idioma que tenga.
/// </summary>
public static class Localization
{
    public const string DefaultCulture = "es";

    /// <summary>Añadir aquí "en" cuando exista Resources/ErrorMessages.en.resx (fase 11).</summary>
    private static readonly string[] SupportedCultures = [DefaultCulture];

    /// <summary>
    /// Los mensajes estándar de FluentValidation ("no debe estar vacío"…) también siguen la
    /// cultura de la petición, sin configuración extra: la leen de CultureInfo.CurrentUICulture,
    /// que es la que fija UseApiLocalization.
    /// </summary>
    public static IServiceCollection AddApiLocalization(this IServiceCollection services) =>
        services.AddLocalization();

    public static IApplicationBuilder UseApiLocalization(this IApplicationBuilder app)
    {
        var options = new RequestLocalizationOptions()
            .SetDefaultCulture(DefaultCulture)
            .AddSupportedCultures(SupportedCultures)
            .AddSupportedUICultures(SupportedCultures);

        // Solo la cabecera Accept-Language. Se descartan los proveedores por query string
        // (?culture=) y por cookie: menos superficie de entrada sin ninguna necesidad real.
        options.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()];

        // Por si el proceso arranca sin petición (tareas en segundo plano, tests): mismo idioma.
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(DefaultCulture);

        return app.UseRequestLocalization(options);
    }
}

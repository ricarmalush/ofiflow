namespace OfiFlow.Api.Common;

/// <summary>
/// Cabeceras de seguridad en todas las respuestas (ADR-009 R6). Es una API JSON que nunca
/// sirve HTML, así que la CSP puede ser la más restrictiva posible.
/// Se registran con OnStarting para que sobrevivan también a las respuestas de error:
/// el ExceptionHandler limpia las cabeceras antes de escribir el 500.
/// </summary>
/// <remarks>
/// Única excepción: la pantalla de Swagger (solo en Development) es HTML con scripts y estilos, y
/// la CSP estricta la dejaría en blanco. Se relaja únicamente para las rutas de esa pantalla y solo
/// en Development; en cualquier otro entorno la ruta ni existe y la CSP no cambia.
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public const string StrictContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>Lo mínimo que necesita Swagger UI: sus propios ficheros, y scripts y estilos incrustados.</summary>
    public const string SwaggerUiContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";

    public const string SwaggerUiPath = "/swagger";

    public Task InvokeAsync(HttpContext context)
    {
        var isSwaggerUi = environment.IsDevelopment() && context.Request.Path.StartsWithSegments(SwaggerUiPath);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = isSwaggerUi ? SwaggerUiContentSecurityPolicy : StrictContentSecurityPolicy;
            // El valor más restrictivo. Hallazgo real del primer escaneo DAST (ADR-010), no una suposición.
            // No estorba al frontend: esta cabecera solo afecta a las cargas que no son CORS (una imagen, un script);
            // las peticiones fetch con CORS (ADR-017) se autorizan con Access-Control-Allow-Origin, no con esta.
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });

        return next(context);
    }
}

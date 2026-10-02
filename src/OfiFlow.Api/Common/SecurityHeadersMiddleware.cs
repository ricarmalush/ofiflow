namespace OfiFlow.Api.Common;

/// <summary>
/// Cabeceras de seguridad en todas las respuestas (ADR-009 R6). Es una API JSON que nunca
/// sirve HTML, así que la CSP puede ser la más restrictiva posible.
/// Se registran con OnStarting para que sobrevivan también a las respuestas de error:
/// el ExceptionHandler limpia las cabeceras antes de escribir el 500.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            // Ningún frontend consume esta API todavía (sin CORS configurado): el valor más
            // restrictivo. Hallazgo real del primer escaneo DAST (ADR-010), no una suposición.
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });

        return next(context);
    }
}

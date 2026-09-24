namespace OfiFlow.Api.Common;

/// <summary>
/// Añade la IP de origen al scope de logging de cada petición, para que los eventos de
/// seguridad de Application/Infrastructure la incluyan sin tener que pasarla por sus
/// interfaces (ADR-009 R5). ASP.NET Core ya añade TraceId y RequestPath al scope.
/// </summary>
public sealed class RequestLogScopeMiddleware(RequestDelegate next, ILogger<RequestLogScopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Plantilla y no diccionario: se lee bien en consola ("SourceIp:1.2.3.4") y un formateador
        // JSON la sigue exportando como campo estructurado.
        using (logger.BeginScope("SourceIp:{SourceIp}", context.Connection.RemoteIpAddress?.ToString()))
        {
            await next(context);
        }
    }
}

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OfiFlow.Application.Common.Logging;

namespace OfiFlow.Api.Common;

/// <summary>
/// Rate limiting de los endpoints anónimos de autenticación (ADR-009 R4): ventana fija por IP,
/// en memoria (una sola instancia; Redis solo cuando exista necesidad real, sección 38).
/// Los valores son iniciales y se revisan con datos reales en la Beta.
/// </summary>
public static partial class RateLimiting
{
    public const string Login = "auth-login";
    public const string Refresh = "auth-refresh";
    public const string Register = "auth-register";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Login, context => PerIp(context, permitLimit: 5, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Refresh, context => PerIp(context, permitLimit: 20, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Register, context => PerIp(context, permitLimit: 3, TimeSpan.FromHours(1)));

            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var httpContext = rejected.HttpContext;
                var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimiting));
                LogRateLimitExceeded(logger, httpContext.Request.Path);

                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Demasiadas peticiones",
                    Detail = "Has superado el número de intentos permitidos. Inténtalo de nuevo más tarde."
                };

                await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
            };
        });

        return services;
    }

    private static RateLimitPartition<string> PerIp(HttpContext context, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Detrás de un proxy inverso hará falta UseForwardedHeaders con proxies de confianza
            // (ADR-009, riesgos aceptados); si no, todos los clientes compartirían la IP del proxy.
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0
            });

    // La IP la añade el scope de RequestLogScopeMiddleware.
    [LoggerMessage(EventId = SecurityEventIds.RateLimitExceeded, Level = LogLevel.Warning,
        Message = "Rate limit superado en {Path}")]
    private static partial void LogRateLimitExceeded(ILogger logger, string path);
}

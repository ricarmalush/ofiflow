using System.Net.Mime;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using OfiFlow.Api.Resources;
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

    /// <summary>Cerrar sesión (anónimo; una consulta por índice y a lo sumo una escritura): 20 por minuto y por IP, con cubo propio para no gastar el de renovar (spec 013).</summary>
    public const string Logout = "auth-logout";

    /// <summary>Cerrar todas mis sesiones (autenticada, rara; una consulta y una escritura): 10 por hora y por IP, con cubo propio (spec 014).</summary>
    public const string AccountSessions = "account-sessions";

    public const string AcceptInvitation = "auth-accept-invitation";

    /// <summary>Canjear el desafío del login en dos pasos (anónimo y con acceso a la base de datos): 10 por minuto y por IP (spec 010).</summary>
    public const string MfaVerify = "auth-mfa-verify";

    /// <summary>Gestionar el segundo factor (pedir el secreto, confirmar, desactivar, regenerar, reiniciar): 10 por hora y por IP (spec 010).</summary>
    public const string MfaManage = "mfa-manage";

    /// <summary>Primer paso del inicio de sesión de plataforma (anónimo y con acceso a la base de datos): 5 por minuto y por IP, como el login de empresa (spec 011).</summary>
    public const string PlatformLogin = "platform-login";

    /// <summary>Canjear el desafío de plataforma: 10 por minuto y por IP, como el de empresa (spec 011).</summary>
    public const string PlatformVerify = "platform-verify";

    /// <summary>Las operaciones del panel con sesión de plataforma (listar, suspender, reactivar): 30 por minuto y por IP (spec 011, valor inicial).</summary>
    public const string PlatformOperations = "platform-operations";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(Login, context => PerIp(context, permitLimit: 5, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Refresh, context => PerIp(context, permitLimit: 20, TimeSpan.FromMinutes(1)));
            options.AddPolicy(Register, context => PerIp(context, permitLimit: 3, TimeSpan.FromHours(1)));
            // Mismo número que renovar pero otro cubo: el frontend cierra sesión siempre (aunque ya no haya sesión) y eso no debe gastar el presupuesto de renovar (spec 013 R8).
            options.AddPolicy(Logout, context => PerIp(context, permitLimit: 20, TimeSpan.FromMinutes(1)));
            // Acción rara y explícita de una persona identificada: pocas por hora basta y frena una sesión robada usada para molestar (spec 014 R7).
            options.AddPolicy(AccountSessions, context => PerIp(context, permitLimit: 10, TimeSpan.FromHours(1)));
            // Anónima y cara (hashea una contraseña con PBKDF2): sin límite sería una vía de agotamiento de CPU (spec 009 D7).
            options.AddPolicy(AcceptInvitation, context => PerIp(context, permitLimit: 10, TimeSpan.FromHours(1)));
            // Probar códigos de 6 dígitos sin freno sería la vía obvia contra el segundo factor: además del tope de 5 intentos por
            // desafío y del bloqueo por cuenta, un límite por IP para quien reparte los intentos entre muchos desafíos (spec 010, valores iniciales).
            options.AddPolicy(MfaVerify, context => PerIp(context, permitLimit: 10, TimeSpan.FromMinutes(1)));
            // Operaciones raras (activar, desactivar, regenerar): pocas por hora es más que suficiente y frena el abuso.
            options.AddPolicy(MfaManage, context => PerIp(context, permitLimit: 10, TimeSpan.FromHours(1)));
            // La plataforma es la cuenta más valiosa: mismos límites que el login y el canje de empresa, con contador propio (spec 011, valores iniciales).
            options.AddPolicy(PlatformLogin, context => PerIp(context, permitLimit: 5, TimeSpan.FromMinutes(1)));
            options.AddPolicy(PlatformVerify, context => PerIp(context, permitLimit: 10, TimeSpan.FromMinutes(1)));
            // Ya autenticadas, pero cada una toca la base de datos de todas las empresas: un tope por IP frena una sesión robada usada como mangueras.
            options.AddPolicy(PlatformOperations, context => PerIp(context, permitLimit: 30, TimeSpan.FromMinutes(1)));

            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var httpContext = rejected.HttpContext;
                var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimiting));
                LogRateLimitExceeded(logger, httpContext.Request.Path);

                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                var messages = httpContext.RequestServices.GetRequiredService<IStringLocalizer<ErrorMessages>>();
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = messages[ProblemTitles.TooManyRequests],
                    Detail = messages[ApiErrors.RateLimitExceeded]
                };
                problem.Extensions["code"] = ApiErrors.RateLimitExceeded;

                await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: MediaTypeNames.Application.ProblemJson, cancellationToken);
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

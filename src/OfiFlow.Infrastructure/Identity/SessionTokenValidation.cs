using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Un access token de empresa solo vale mientras su sesión siga activa (spec 015). JwtBearer ya comprobó la firma, el emisor, la
/// audiencia y la caducidad; aquí se comprueba lo que un JWT no puede saber por sí solo: si la sesión que lo emitió se cerró.
/// Va en la <b>autenticación</b> y no en la autorización a propósito (D2): un fallo aquí es un <c>401</c>, que es lo único ante lo que
/// el frontend renueva la sesión o vuelve al login; en el AuthorizationBehavior sería un <c>403</c>, y el usuario se quedaría ante errores
/// sueltos. Es fail-closed: un token sin identificador de sesión, o con uno que no es un Guid, se rechaza (en la práctica, los emitidos
/// antes de la spec 015, que caducan en 30 minutos y que el frontend renueva solo con su refresh token).
/// </summary>
/// <remarks>
/// Solo se conecta al esquema de las empresas. La sesión de plataforma (spec 011) no tiene refresh token y se comprueba por otra vía
/// (que el operador siga activo, en cada petición).
/// </remarks>
internal static partial class SessionTokenValidation
{
    public static class Reasons
    {
        /// <summary>El token no trae «sid», o no es un Guid.</summary>
        public const string MissingSessionId = "MissingSessionId";

        /// <summary>La sesión del token no tiene ningún refresh token activo (se cerró, o no existe, o es de otra cuenta).</summary>
        public const string SessionNotActive = "SessionNotActive";
    }

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var userId = Guid.TryParse(principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var parsedUser) ? parsedUser : (Guid?)null;

        if (!Guid.TryParse(principal?.FindFirst(OfiFlowClaimTypes.SessionId)?.Value, out var sessionId))
        {
            Reject(context, Reasons.MissingSessionId, userId);
            return;
        }

        // Sin un «sub» válido no hay cuenta a la que atribuir la sesión: no se consulta nada y se rechaza.
        var active = userId is not null
            && await context.HttpContext.RequestServices.GetRequiredService<ITokenService>()
                .IsSessionActiveAsync(userId.Value, sessionId, context.HttpContext.RequestAborted);

        if (!active)
        {
            Reject(context, Reasons.SessionNotActive, userId);
        }
    }

    private static void Reject(TokenValidatedContext context, string reason, Guid? userId)
    {
        // Ni el token ni el identificador de sesión van al registro (ADR-009 R5): solo el motivo y, si se conoce, la cuenta.
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionTokenValidation).FullName!);
        LogSessionTokenRejected(logger, reason, userId);

        // El texto no sale al cliente: la respuesta es un 401 y nada más que lo habitual de JwtBearer.
        context.Fail("session");
    }

    [LoggerMessage(EventId = SecurityEventIds.SessionTokenRejected, Level = LogLevel.Information,
        Message = "Access token rechazado por su sesión. Motivo {Reason}, UserId {UserId}")]
    private static partial void LogSessionTokenRejected(ILogger logger, string reason, Guid? userId);
}

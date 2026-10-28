using MediatR;
using OfiFlow.Api.Common;
using OfiFlow.Application.Identity.Commands.CloseAllSessions;
using OfiFlow.Application.Identity.Queries.GetCurrentAccount;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Datos y sesiones de la <b>propia</b> cuenta (specs 012 y 014). Ninguna ruta tiene un Id ni un parámetro: la cuenta sale del token,
/// así que nadie puede preguntar por otra persona, ni cerrarle las sesiones, por aquí. Exige una sesión de <b>empresa</b> (un token de plataforma es otro mundo y recibe
/// un 401); el permiso <c>Account.Read</c> lo comprueba el AuthorizationBehavior, que además verifica en la base de datos la
/// pertenencia y el rol (y que la empresa no esté suspendida). El segundo factor de la cuenta tiene su propio grupo,
/// <see cref="MfaEndpoints"/>.
/// </summary>
public static class AccountEndpoints
{
    public const string Route = "/api/v1/account";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Account").RequireAuthorization();

        group.MapGet("/me", async (ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var account = await sender.Send(new GetCurrentAccountQuery(), cancellationToken);

                // Es personal (email, nombre): ninguna caché, ni del navegador ni intermedia, debe guardarla.
                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Ok(account);
            })
            .WithName("GetCurrentAccount")
            .WithSummary("Quién eres: tu nombre y email, tu empresa, el Id de tu pertenencia, tu rol EFECTIVO (el de la base de datos, no el del token) y los permisos de ese rol. Los permisos sirven para pintar la interfaz; quien decide es siempre la API.");

        // Cerrar todas mis sesiones (spec 014). Sin Id, cuerpo ni parámetros: la cuenta sale del token. Cuelga de este grupo (autenticado)
        // y no de /auth porque el grupo /auth es anónimo y su marca gana a RequireAuthorization() (lección de la spec 013).
        group.MapDelete("/sessions", async (ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                await sender.Send(new CloseAllSessionsCommand(), cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";

                // 204 haya o no sesiones activas (R4): idempotente, y quien pide ya sabe quién es.
                return Results.NoContent();
            })
            .WithName("CloseAllSessions")
            .WithSummary("Cierra TODAS tus sesiones (revoca todos tus refresh tokens, en todos tus dispositivos), incluida la desde la que lo pides. Sin parámetros: siempre habla de quien la pide. Responde 204 aunque no hubiera ninguna. Los access tokens ya emitidos siguen valiendo hasta que caduquen (30 min), pero ya no se pueden renovar.")
            .RequireRateLimiting(RateLimiting.AccountSessions);
    }
}

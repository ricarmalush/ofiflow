using MediatR;
using OfiFlow.Api.Common;
using OfiFlow.Application.Identity.Commands.CloseAllSessions;
using OfiFlow.Application.Identity.Commands.CloseSession;
using OfiFlow.Application.Identity.Queries.GetCurrentAccount;
using OfiFlow.Application.Identity.Queries.ListMySessions;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Datos y sesiones de la <b>propia</b> cuenta (specs 012, 014 y 016). La cuenta sale siempre del token, nunca de la ruta: ninguna ruta lleva el Id de una
/// cuenta, así que nadie puede preguntar por otra persona, ni cerrarle las sesiones, por aquí. La única que lleva un Id es <c>DELETE /sessions/{id}</c>, y ese Id
/// solo elige <b>cuál de MIS sesiones</b> cerrar (spec 016 R13): una sesión ajena, una inexistente y una ya cerrada dan el mismo 404. Exige una sesión de <b>empresa</b> (un token de plataforma es otro mundo y recibe
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
            .WithSummary("Cierra TODAS tus sesiones (revoca todos tus refresh tokens, en todos tus dispositivos), incluida la desde la que lo pides. Sin parámetros: siempre habla de quien la pide. Responde 204. Desde la spec 015 los access tokens de todas esas sesiones dejan de valer al instante (401), no solo su renovación.")
            .RequireRateLimiting(RateLimiting.AccountSessions);

        // Mis sesiones activas (spec 016). Sin parámetros: la cuenta sale del token. Lee la propia cuenta (Account.Read, los cinco roles).
        group.MapGet("/sessions", async (ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var sessions = await sender.Send(new ListMySessionsQuery(), cancellationToken);

                // Dónde y desde qué navegador está conectada la persona: personal, como /me. Ninguna caché debe guardarlo.
                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Ok(sessions);
            })
            .WithName("ListMySessions")
            .WithSummary("Tus sesiones activas, de la más reciente a la más antigua: dispositivo («Chrome en Windows»), cuándo empezó, su última actividad (la última renovación, como mucho cada 30 min de uso), la red aproximada (IP truncada) y cuál es ESTA. Nunca devuelve tokens, el navegador crudo ni la IP completa.");

        // Cerrar UNA sesión concreta mía (spec 016). El Id solo elige cuál de las MÍAS: la cuenta sale del token. Comparte cubo de límite con «cerrar todas».
        group.MapDelete("/sessions/{id:guid}", async (Guid id, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                await sender.Send(new CloseSessionCommand(id), cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.NoContent();
            })
            .WithName("CloseSession")
            .WithSummary("Cierra UNA de tus sesiones (revoca su refresh token) por su Id, la de otro dispositivo o la actual. Responde 204, o 404 idéntico si la sesión no existe, es de otra cuenta o ya estaba cerrada. Su access token deja de valer en la petición siguiente.")
            .RequireRateLimiting(RateLimiting.AccountSessions);
    }
}

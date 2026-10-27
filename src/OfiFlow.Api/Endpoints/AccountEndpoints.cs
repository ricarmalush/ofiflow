using MediatR;
using OfiFlow.Application.Identity.Queries.GetCurrentAccount;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Datos de la <b>propia</b> cuenta (spec 012). La ruta no tiene ningún Id ni parámetro: la cuenta sale del token, así que nadie
/// puede preguntar por otra persona por aquí. Exige una sesión de <b>empresa</b> (un token de plataforma es otro mundo y recibe
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
    }
}

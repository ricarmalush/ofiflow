using MediatR;
using OfiFlow.Api.Common;
using OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;
using OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;
using OfiFlow.Application.Identity.Mfa.Queries.GetMfaStatus;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// El segundo factor de la <b>propia</b> cuenta (spec 010). No hay ningún Id en las rutas: la cuenta sale del token, así
/// que nadie puede tocar el segundo factor de otra persona por aquí (reiniciar el de un miembro es
/// <c>DELETE /api/v1/users/{id}/mfa</c>, en <see cref="UserEndpoints"/>). Exigen sesión; el permiso <c>Mfa.Manage</c> lo comprueba el
/// AuthorizationBehavior. La operación anónima de canjear el desafío del login está en <see cref="AuthEndpoints"/>.
/// </summary>
public static class MfaEndpoints
{
    public const string Route = "/api/v1/account/mfa";

    public static void MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Account MFA").RequireAuthorization();

        group.MapGet("/", async (ISender sender, CancellationToken cancellationToken) =>
            {
                var status = await sender.Send(new GetMfaStatusQuery(), cancellationToken);
                return Results.Ok(status);
            })
            .WithName("GetMfaStatus")
            .WithSummary("Cómo está el segundo factor de tu cuenta: activo, pendiente de confirmar y cuántos códigos de recuperación quedan. Nunca devuelve secretos.");

        group.MapPost("/setup", async (StartMfaSetupCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var setup = await sender.Send(command, cancellationToken);
                NoStore(httpContext);

                return Results.Ok(setup);
            })
            .WithName("StartMfaSetup")
            .WithSummary("Pide el secreto para activar el segundo factor (exige tu contraseña). Devuelve el secreto y la URI otpauth:// UNA sola vez; hay que confirmarlo con un código.")
            .RequireRateLimiting(RateLimiting.MfaManage);

        group.MapPost("/enable", async (EnableMfaCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var codes = await sender.Send(command, cancellationToken);
                NoStore(httpContext);

                return Results.Ok(codes);
            })
            .WithName("EnableMfa")
            .WithSummary("Confirma el secreto con el primer código de la app y activa el segundo factor. Devuelve los códigos de recuperación UNA sola vez y cierra todas tus sesiones.")
            .RequireRateLimiting(RateLimiting.MfaManage);

        group.MapPost("/disable", async (DisableMfaCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);
                return Results.NoContent();
            })
            .WithName("DisableMfa")
            .WithSummary("Desactiva el segundo factor (exige tu contraseña y un código de la app o de recuperación) y cierra todas tus sesiones.")
            .RequireRateLimiting(RateLimiting.MfaManage);

        group.MapPost("/recovery-codes", async (RegenerateRecoveryCodesCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var codes = await sender.Send(command, cancellationToken);
                NoStore(httpContext);

                return Results.Ok(codes);
            })
            .WithName("RegenerateRecoveryCodes")
            .WithSummary("Genera códigos de recuperación nuevos (exige tu contraseña y un código); los anteriores dejan de valer. Se muestran UNA sola vez.")
            .RequireRateLimiting(RateLimiting.MfaManage);
    }

    /// <summary>
    /// La respuesta lleva un secreto (o los códigos de recuperación) que no se vuelve a mostrar nunca: que ningún navegador
    /// ni proxy la guarde en una caché.
    /// </summary>
    private static void NoStore(HttpContext httpContext) => httpContext.Response.Headers.CacheControl = "no-store";
}

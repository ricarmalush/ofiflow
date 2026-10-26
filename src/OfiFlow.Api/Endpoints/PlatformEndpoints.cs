using MediatR;
using OfiFlow.Api.Common;
using OfiFlow.Application.Platform.Commands.PlatformLogin;
using OfiFlow.Application.Platform.Commands.ReactivateTenant;
using OfiFlow.Application.Platform.Commands.SuspendTenant;
using OfiFlow.Application.Platform.Commands.VerifyPlatformChallenge;
using OfiFlow.Application.Platform.Queries.ListTenants;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Las rutas de la plataforma (spec 011 R14, ADR-016): todas cuelgan de <c>/api/v1/platform</c>. El inicio de sesión del operador es anónimo
/// (la contraseña y el código son las credenciales); las operaciones sobre empresas (listar, suspender, reactivar) exigen la política
/// de plataforma, que solo acepta una sesión del esquema de plataforma. El alta y la baja de un operador
/// <b>no tienen ruta</b>: se hacen con el programa del servidor.
/// </summary>
public static class PlatformEndpoints
{
    public const string Route = "/api/v1/platform";

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup(Route + "/auth").WithTags("Platform").AllowAnonymous();

        auth.MapPost("/login", async (PlatformLoginCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var challenge = await sender.Send(command, cancellationToken);
                if (challenge is null)
                {
                    // Un único 401 para todos los fallos (R6): ni el cuerpo ni el estado dicen cuál fue.
                    return Results.Unauthorized();
                }

                // Nunca hay sesión en este paso (R5): solo un desafío. Contiene un valor secreto que no se vuelve a mostrar.
                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new { mfaRequired = true, challenge = challenge.Challenge, expiresAt = challenge.ExpiresAt });
            })
            .WithName("PlatformLogin")
            .WithSummary("Primer paso del inicio de sesión de un operador de la plataforma. Siempre devuelve 200 { mfaRequired, challenge, expiresAt } (sin sesión) o un único 401 para cualquier fallo.")
            .RequireRateLimiting(RateLimiting.PlatformLogin);

        auth.MapPost("/verify", async (VerifyPlatformChallengeCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var session = await sender.Send(command, cancellationToken);
                if (session is null)
                {
                    return Results.Unauthorized();
                }

                // La sesión no se vuelve a mostrar: ninguna caché debe guardarla.
                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Ok(session);
            })
            .WithName("VerifyPlatformChallenge")
            .WithSummary("Segundo paso: canjea el desafío con un código de la app o de recuperación y devuelve la sesión de plataforma (15 minutos, sin refresh token). Todos los fallos dan el mismo 401.")
            .RequireRateLimiting(RateLimiting.PlatformVerify);

        // Las operaciones del panel: la política exige el esquema de plataforma (un token de empresa no pasa de aquí con un 401) y,
        // además, cada operación declara [RequiresPlatformOperator] y revalida en la base de datos que el operador sigue activo.
        var tenants = app.MapGroup(Route + "/tenants")
            .WithTags("Platform")
            .RequireAuthorization(PlatformAuthentication.Policy)
            .RequireRateLimiting(RateLimiting.PlatformOperations);

        tenants.MapGet("/", async (int? page, int? pageSize, bool? suspended, ISender sender, CancellationToken cancellationToken) =>
                Results.Ok(await sender.Send(new ListTenantsQuery(page ?? 1, pageSize ?? ListTenantsQuery.DefaultPageSize, suspended), cancellationToken)))
            .WithName("ListTenants")
            .WithSummary("Lista las empresas con sus metadatos: Id, nombre, alta, número de miembros y estado. Nada más. Paginada (pageSize ≤ 100), alta descendente.");

        tenants.MapPost("/{tenantId:guid}/suspend", async (Guid tenantId, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new SuspendTenantCommand(tenantId), cancellationToken);
                return Results.NoContent();
            })
            .WithName("SuspendTenant")
            .WithSummary("Suspende una empresa: sus miembros dejan de poder entrar. No toca sus datos. 400 si ya estaba suspendida, 404 si no existe.");

        tenants.MapPost("/{tenantId:guid}/reactivate", async (Guid tenantId, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new ReactivateTenantCommand(tenantId), cancellationToken);
                return Results.NoContent();
            })
            .WithName("ReactivateTenant")
            .WithSummary("Reactiva una empresa suspendida. 400 si no estaba suspendida, 404 si no existe.");
    }
}

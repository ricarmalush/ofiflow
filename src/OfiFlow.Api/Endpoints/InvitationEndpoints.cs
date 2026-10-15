using MediatR;
using OfiFlow.Application.Tenancy.Commands.InviteUser;
using OfiFlow.Application.Tenancy.Commands.RevokeInvitation;
using OfiFlow.Application.Tenancy.Queries.GetInvitations;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Invitar a una persona a la empresa (spec 009). Exigen sesión; quién puede hacerlo lo decide el
/// AuthorizationBehavior (permiso <c>Users.Manage</c> y jerarquía de roles), no el endpoint. La operación
/// anónima de aceptar está en <see cref="AuthEndpoints"/>.
/// </summary>
public static class InvitationEndpoints
{
    public const string Route = "/api/v1/invitations";

    public static void MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Invitations").RequireAuthorization();

        group.MapPost("/", async (InviteUserCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var invitation = await sender.Send(command, cancellationToken);

                // La respuesta lleva el token en claro, que no se vuelve a mostrar nunca: que ningún navegador ni proxy
                // la guarde en una caché.
                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Json(invitation, statusCode: StatusCodes.Status201Created);
            })
            .WithName("InviteUser")
            .WithSummary("Invita a una persona a la empresa con un rol. Devuelve el token de la invitación, una única vez.");

        group.MapGet("/", async (ISender sender, CancellationToken cancellationToken) =>
            {
                var invitations = await sender.Send(new GetInvitationsQuery(), cancellationToken);
                return Results.Ok(invitations);
            })
            .WithName("GetInvitations")
            .WithSummary("Lista las invitaciones pendientes de la empresa (sin el token).");

        group.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new RevokeInvitationCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("RevokeInvitation")
            .WithSummary("Revoca una invitación pendiente.");
    }
}

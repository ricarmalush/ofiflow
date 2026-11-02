using MediatR;
using OfiFlow.Api.Common;
using OfiFlow.Application.Common.Abstractions;
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

        group.MapPost("/", async (InviteUserCommand command, ISender sender, ICurrentUser currentUser, InvitationThrottle throttle, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                // Spec 017 D10: 20 invitaciones por hora y por persona (enviar correo es abusable). Va aquí y no en el limitador global porque la clave es la persona, que solo se conoce
                // ya autenticada. Un token sin UserId no llega: el grupo exige autorización.
                if (currentUser.UserId is { } userId && !throttle.TryAcquire(userId, out var retryAfter))
                {
                    await RateLimiting.RejectAsync(httpContext, retryAfter, cancellationToken);
                    return Results.Empty;
                }

                var invitation = await sender.Send(command, cancellationToken);

                // Sin correo configurado, la respuesta lleva el token en claro, que no se vuelve a mostrar nunca: que ningún navegador ni proxy
                // la guarde en una caché. (Con correo no lleva token, pero la respuesta sigue sin guardarse: no hay motivo para distinguirlas.)
                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Json(invitation, statusCode: StatusCodes.Status201Created);
            })
            .WithName("InviteUser")
            .WithSummary("Invita a una persona a la empresa con un rol. Con correo configurado le envía el enlace (emailSent: true, sin token); sin él devuelve el token de la invitación, una única vez.");

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

using MediatR;
using OfiFlow.Application.Tenancy.Commands.ChangeUserRole;
using OfiFlow.Application.Tenancy.Commands.RemoveUser;
using OfiFlow.Application.Tenancy.Queries.GetUsers;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Los miembros de la empresa (spec 009). El Id de la ruta es el de la **pertenencia** (el que devuelve el listado), no
/// el de la cuenta. Quién puede cambiar o quitar a quién lo decide la jerarquía de roles en Application.
/// </summary>
public static class UserEndpoints
{
    public const string Route = "/api/v1/users";

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Users").RequireAuthorization();

        group.MapGet("/", async (ISender sender, CancellationToken cancellationToken) =>
            {
                var members = await sender.Send(new GetUsersQuery(), cancellationToken);
                return Results.Ok(members);
            })
            .WithName("GetUsers")
            .WithSummary("Lista a los miembros de la empresa con su rol.");

        group.MapPut("/{id:guid}/role", async (Guid id, ChangeUserRoleRequest request, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new ChangeUserRoleCommand(id, request.Role), cancellationToken);
                return Results.NoContent();
            })
            .WithName("ChangeUserRole")
            .WithSummary("Cambia el rol de un miembro. No se puede cambiar el propio ni el del Owner, y un Admin no gestiona a otros Admin.");

        group.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new RemoveUserCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("RemoveUser")
            .WithSummary("Quita a un miembro de la empresa: se borran su cuenta y sus datos y se desasignan sus trabajos sin terminar.");
    }
}

/// <summary>El cuerpo de "cambiar rol": el Id va en la ruta, nunca en el cuerpo (ver <c>AssignJobRequest</c>).</summary>
public sealed record ChangeUserRoleRequest(TenantRole Role);

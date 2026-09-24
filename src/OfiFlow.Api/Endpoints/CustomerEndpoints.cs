using MediatR;
using OfiFlow.Application.Customers.Commands.CreateCustomer;
using OfiFlow.Application.Customers.Commands.DeleteCustomer;
using OfiFlow.Application.Customers.Commands.UpdateCustomer;
using OfiFlow.Application.Customers.Queries.GetCustomer;
using OfiFlow.Application.Customers.Queries.GetCustomers;

namespace OfiFlow.Api.Endpoints;

public static class CustomerEndpoints
{
    public const string Route = "/api/v1/customers";

    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Customers").RequireAuthorization();

        group.MapPost("/", async (CreateCustomerCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var id = await sender.Send(command, cancellationToken);
                return Results.Created($"{Route}/{id}", new { id });
            })
            .WithName("CreateCustomer")
            .WithSummary("Crea un nuevo cliente del tenant activo.");

        group.MapGet("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                var customer = await sender.Send(new GetCustomerQuery(id), cancellationToken);
                return customer is null ? Results.NotFound() : Results.Ok(customer);
            })
            .WithName("GetCustomer")
            .WithSummary("Consulta un cliente por Id.");

        group.MapGet("/", async (ISender sender, CancellationToken cancellationToken) =>
            {
                var customers = await sender.Send(new GetCustomersQuery(), cancellationToken);
                return Results.Ok(customers);
            })
            .WithName("GetCustomers")
            .WithSummary("Lista los clientes del tenant activo.");

        group.MapPut("/{id:guid}", async (Guid id, UpdateCustomerRequest request, ISender sender, CancellationToken cancellationToken) =>
            {
                var command = new UpdateCustomerCommand(id, request.Name, request.Email, request.Phone, request.Address, request.Notes);
                await sender.Send(command, cancellationToken);
                return Results.NoContent();
            })
            .WithName("UpdateCustomer")
            .WithSummary("Actualiza los datos de contacto de un cliente.");

        group.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new DeleteCustomerCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteCustomer")
            .WithSummary("Elimina un cliente del tenant activo.");
    }

    internal sealed record UpdateCustomerRequest(string Name, string? Email, string? Phone, string? Address, string? Notes);
}

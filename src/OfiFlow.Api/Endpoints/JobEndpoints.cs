using MediatR;
using OfiFlow.Application.Jobs.Commands.AssignJob;
using OfiFlow.Application.Jobs.Commands.CancelJob;
using OfiFlow.Application.Jobs.Commands.CompleteJob;
using OfiFlow.Application.Jobs.Commands.CreateJob;
using OfiFlow.Application.Jobs.Commands.StartJob;
using OfiFlow.Application.Jobs.Commands.UpdateJob;
using OfiFlow.Application.Jobs.Queries.GetJob;
using OfiFlow.Application.Jobs.Queries.GetJobs;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Api.Endpoints;

public static class JobEndpoints
{
    public const string Route = "/api/v1/jobs";

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Jobs").RequireAuthorization();

        group.MapPost("/", async (CreateJobCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var id = await sender.Send(command, cancellationToken);
                return Results.Created($"{Route}/{id}", new { id });
            })
            .WithName("CreateJob")
            .WithSummary("Crea un trabajo para un cliente del tenant activo.");

        group.MapGet("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                var job = await sender.Send(new GetJobQuery(id), cancellationToken);
                return job is null ? Results.NotFound() : Results.Ok(job);
            })
            .WithName("GetJob")
            .WithSummary("Consulta un trabajo por Id.");

        group.MapGet("/", async (ISender sender, CancellationToken cancellationToken) =>
            {
                var jobs = await sender.Send(new GetJobsQuery(), cancellationToken);
                return Results.Ok(jobs);
            })
            .WithName("GetJobs")
            .WithSummary("Lista los trabajos del tenant activo.");

        group.MapPut("/{id:guid}", async (Guid id, UpdateJobRequest request, ISender sender, CancellationToken cancellationToken) =>
            {
                var command = new UpdateJobCommand(id, request.Title, request.Description, request.Priority);
                await sender.Send(command, cancellationToken);
                return Results.NoContent();
            })
            .WithName("UpdateJob")
            .WithSummary("Actualiza título, descripción y prioridad de un trabajo.");

        group.MapPost("/{id:guid}/start", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new StartJobCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("StartJob")
            .WithSummary("Inicia un trabajo (New → InProgress).");

        group.MapPost("/{id:guid}/complete", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new CompleteJobCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("CompleteJob")
            .WithSummary("Completa un trabajo (InProgress → Completed).");

        group.MapPost("/{id:guid}/cancel", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new CancelJobCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("CancelJob")
            .WithSummary("Cancela un trabajo (cualquier estado salvo Completed → Cancelled).");

        group.MapPost("/{id:guid}/assign", async (Guid id, AssignJobRequest request, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new AssignJobCommand(id, request.TenantUserId), cancellationToken);
                return Results.NoContent();
            })
            .WithName("AssignJob")
            .WithSummary("Asigna un trabajo a un TenantUser del tenant activo.");
    }

    internal sealed record UpdateJobRequest(string Title, string? Description, JobPriority Priority);

    internal sealed record AssignJobRequest(Guid TenantUserId);
}

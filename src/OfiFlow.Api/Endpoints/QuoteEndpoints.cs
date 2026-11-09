using MediatR;
using OfiFlow.Application.Quotes.Commands.AcceptQuote;
using OfiFlow.Application.Quotes.Commands.CreateQuote;
using OfiFlow.Application.Quotes.Commands.DeleteQuote;
using OfiFlow.Application.Quotes.Commands.RejectQuote;
using OfiFlow.Application.Quotes.Commands.SendQuote;
using OfiFlow.Application.Quotes.Commands.UpdateQuote;
using OfiFlow.Application.Quotes.Queries.GetQuote;
using OfiFlow.Application.Quotes.Queries.GetQuotes;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// Los presupuestos (spec 023). Todo va bajo sesión y la empresa sale del token, nunca de la petición. <b>Ninguna petición lleva un total, un número ni un estado</b>:
/// los importes los calcula el servidor (R5), el número lo asigna él (R6) y el estado solo cambia por las tres transiciones. Un campo de más en el cuerpo se ignora.
/// </summary>
public static class QuoteEndpoints
{
    public const string Route = "/api/v1/quotes";

    public static void MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Quotes").RequireAuthorization();

        group.MapPost("/", async (CreateQuoteCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var id = await sender.Send(command, cancellationToken);
                return Results.Created($"{Route}/{id}", new { id });
            })
            .WithName("CreateQuote")
            .WithSummary("Crea un borrador de presupuesto para un cliente (con trabajo opcional del mismo cliente). El número y los totales los pone el servidor. Importes en céntimos; IVA 0, 4, 10 o 21.");

        group.MapGet("/", async (QuoteStatus? status, Guid? customerId, HttpContext httpContext, ISender sender, CancellationToken cancellationToken, int page = 1, int pageSize = GetQuotesQuery.DefaultPageSize) =>
            {
                // Los precios no se guardan en cachés intermedias ni en la del navegador (spec 023).
                httpContext.Response.Headers.CacheControl = "no-store";
                var quotes = await sender.Send(new GetQuotesQuery(status, customerId, page, pageSize), cancellationToken);
                return Results.Ok(quotes);
            })
            .WithName("GetQuotes")
            .WithSummary("Lista los presupuestos de la empresa, del más reciente al más antiguo, paginados (hasta 100 por página), filtrables por estado y por cliente. Sin las líneas.");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext httpContext, ISender sender, CancellationToken cancellationToken) =>
            {
                httpContext.Response.Headers.CacheControl = "no-store";
                var quote = await sender.Send(new GetQuoteQuery(id), cancellationToken);
                return Results.Ok(quote);
            })
            .WithName("GetQuote")
            .WithSummary("Consulta un presupuesto con sus líneas, el desglose de IVA y su versión (para modificarlo).");

        group.MapPut("/{id:guid}", async (Guid id, UpdateQuoteRequest request, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new UpdateQuoteCommand(id, request.Version, request.JobId, request.Title, request.Notes, request.ValidUntil, request.Items), cancellationToken);
                return Results.NoContent();
            })
            .WithName("UpdateQuote")
            .WithSummary("Modifica un borrador: sustituye la cabecera y todas las líneas. Exige la versión que se leyó; si cambió mientras tanto, no se pisa nada.");

        group.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new DeleteQuoteCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteQuote")
            .WithSummary("Elimina un borrador (un presupuesto enviado no se elimina).");

        group.MapPost("/{id:guid}/send", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new SendQuoteCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("SendQuote")
            .WithSummary("Marca un borrador como enviado (Draft → Sent). Exige al menos una línea. No manda nada al cliente: solo lo anota.");

        group.MapPost("/{id:guid}/accept", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new AcceptQuoteCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("AcceptQuote")
            .WithSummary("Acepta un presupuesto enviado (Sent → Accepted). Es una anotación interna, no la firma del cliente.");

        group.MapPost("/{id:guid}/reject", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(new RejectQuoteCommand(id), cancellationToken);
                return Results.NoContent();
            })
            .WithName("RejectQuote")
            .WithSummary("Rechaza un presupuesto enviado (Sent → Rejected).");
    }

    // La versión es la que devolvió la lectura (base64): sin ella no se modifica (spec 023 R10).
    internal sealed record UpdateQuoteRequest(
        string Version,
        Guid? JobId,
        string Title,
        string? Notes,
        DateOnly? ValidUntil,
        IReadOnlyList<QuoteItemInput> Items);
}

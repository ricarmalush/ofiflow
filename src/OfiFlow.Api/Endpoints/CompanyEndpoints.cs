using MediatR;
using OfiFlow.Application.Company.Commands.UpsertCompanyProfile;
using OfiFlow.Application.Company.Queries.GetCompanyProfile;

namespace OfiFlow.Api.Endpoints;

/// <summary>
/// El perfil fiscal de la empresa (spec 024): quién emite los documentos. Todo va bajo sesión y la empresa sale <b>solo del token</b>, nunca de la petición: el cuerpo no tiene ningún
/// campo de empresa y uno de más se ignora. El NIF, la dirección y el teléfono son datos personales cuando la empresa es un autónomo: la lectura no se guarda en cachés y nada de esto llega a un registro.
/// </summary>
public static class CompanyEndpoints
{
    public const string Route = "/api/v1/company/profile";

    public static void MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Company").RequireAuthorization();

        group.MapGet("/", async (HttpContext httpContext, ISender sender, CancellationToken cancellationToken) =>
            {
                // Datos personales de un autónomo: ni caché intermedia ni la del navegador (spec 024).
                httpContext.Response.Headers.CacheControl = "no-store";
                var profile = await sender.Send(new GetCompanyProfileQuery(), cancellationToken);
                return Results.Ok(profile);
            })
            .WithName("GetCompanyProfile")
            .WithSummary("Consulta el perfil fiscal de la empresa (nombre fiscal, NIF/NIE/CIF, dirección, teléfono y email). Si todavía no lo ha guardado, devuelve todos los campos a null e isComplete=false.");

        group.MapPut("/", async (UpsertCompanyProfileCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);
                return Results.NoContent();
            })
            .WithName("UpsertCompanyProfile")
            .WithSummary("Crea el perfil fiscal la primera vez y lo sustituye entero después (un opcional ausente borra el anterior). El NIF se valida con su dígito o letra de control y se guarda normalizado.");
    }
}

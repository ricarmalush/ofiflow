using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Queries.GetAgenda;

/// <summary>
/// La agenda: los trabajos con fecha dentro de <c>[From, To)</c> (spec 020 R11). Los dos extremos llegan como <b>texto ISO-8601 con
/// desfase explícito</b>, igual que la fecha de programar: un extremo sin desfase se rechaza en vez de leerse con el huso del servidor.
/// Leerla es <c>Jobs.Read</c>: la ven los cinco roles.
/// </summary>
[RequiresPermission(Permission.JobsRead)]
public sealed record GetAgendaQuery(string From, string To) : IRequest<List<JobDto>>
{
    /// <summary>
    /// Lo más largo que puede ser el rango de una petición: seis semanas, lo que cabe en una vista de mes. Acota lo que una sola
    /// petición puede devolver (spec 020 R11).
    /// </summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(42);
}

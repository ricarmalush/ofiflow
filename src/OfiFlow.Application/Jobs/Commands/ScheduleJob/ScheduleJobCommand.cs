using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.ScheduleJob;

/// <summary>
/// Programa (o reprograma) un trabajo (spec 020). La fecha llega como <b>texto ISO-8601 con desfase explícito</b> y se valida y
/// convierte a UTC en Application: así una hora sin desfase se rechaza con un 400 de campo, en vez de interpretarse con el huso
/// del servidor. Sustituye la programación entera: una duración omitida borra la anterior (R3).
/// </summary>
[RequiresPermission(Permission.JobsSchedule)]
public sealed record ScheduleJobCommand(Guid Id, string ScheduledDate, int? EstimatedDurationMinutes) : IRequest;

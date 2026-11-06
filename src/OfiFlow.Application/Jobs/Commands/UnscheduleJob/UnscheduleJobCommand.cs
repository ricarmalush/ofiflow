using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.UnscheduleJob;

/// <summary>Quita un trabajo de la agenda (spec 020 R7): vuelve a New sin fecha ni duración.</summary>
[RequiresPermission(Permission.JobsSchedule)]
public sealed record UnscheduleJobCommand(Guid Id) : IRequest;

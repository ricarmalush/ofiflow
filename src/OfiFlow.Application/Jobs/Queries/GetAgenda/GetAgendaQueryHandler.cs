using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Queries.GetAgenda;

public sealed class GetAgendaQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetAgendaQuery, List<JobDto>>
{
    public Task<List<JobDto>> Handle(GetAgendaQuery request, CancellationToken cancellationToken)
    {
        // El validador ya comprobó la forma y el rango; Parse lanza si alguien llamó al handler sin pasar por él.
        var from = IsoInstant.Parse(request.From);
        var to = IsoInstant.Parse(request.To);

        // El filtro global de tenant restringe la consulta a la empresa activa (R13). Semiabierto: un trabajo a las 00:00 exactas de «to»
        // es del día siguiente. Solo salen los que tienen fecha y siguen vivos o hechos: los cancelados y los nuevos (sin fecha) no.
        return dbContext.Jobs
            .Where(j => j.ScheduledDate >= from && j.ScheduledDate < to)
            .Where(j => j.Status == JobStatus.Scheduled || j.Status == JobStatus.InProgress || j.Status == JobStatus.Completed)
            .OrderBy(j => j.ScheduledDate)
            .ThenBy(j => j.Id) // orden estable cuando dos trabajos empiezan a la vez
            .Select(j => new JobDto(
                j.Id,
                j.CustomerId,
                j.Title,
                j.Description,
                j.Status.ToString(),
                j.Priority.ToString(),
                j.ScheduledDate,
                j.AssignedTenantUserId,
                j.EstimatedDurationMinutes))
            .ToListAsync(cancellationToken);
    }
}

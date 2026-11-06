using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Common.Validation;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.ScheduleJob;

public sealed class ScheduleJobCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<ScheduleJobCommand>
{
    public async Task Handle(ScheduleJobCommand request, CancellationToken cancellationToken)
    {
        // El filtro global de tenant restringe la búsqueda: un trabajo de otra empresa es un 404 (R13).
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(JobErrors.NotFound, request.Id);

        // El validador ya comprobó la forma y las cotas; Parse lanza si alguien llamó al handler sin pasar por él.
        // Si el estado no admite programar, Domain lanza DomainException con su código y la API responde 400.
        job.Schedule(IsoInstant.Parse(request.ScheduledDate), request.EstimatedDurationMinutes);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

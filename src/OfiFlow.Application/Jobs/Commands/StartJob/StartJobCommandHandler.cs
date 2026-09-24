using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.StartJob;

public sealed class StartJobCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<StartJobCommand>
{
    public async Task Handle(StartJobCommand request, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(JobErrors.NotFound, request.Id);

        // Si la transición no es válida, Domain lanza DomainException con su código (ADR-011);
        // la API la convierte en 400. No se captura aquí: así nunca se confunde con un bug.
        job.Start();

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

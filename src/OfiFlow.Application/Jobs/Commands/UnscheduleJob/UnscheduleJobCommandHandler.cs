using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.UnscheduleJob;

public sealed class UnscheduleJobCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<UnscheduleJobCommand>
{
    public async Task Handle(UnscheduleJobCommand request, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(JobErrors.NotFound, request.Id);

        job.Unschedule();

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

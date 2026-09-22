using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.CancelJob;

public sealed class CancelJobCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<CancelJobCommand>
{
    public async Task Handle(CancelJobCommand request, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(nameof(Job), request.Id);

        try
        {
            job.Cancel();
        }
        catch (InvalidOperationException ex)
        {
            throw new BusinessRuleException(ex.Message);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

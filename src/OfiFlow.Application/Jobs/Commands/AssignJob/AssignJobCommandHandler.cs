using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.AssignJob;

public sealed class AssignJobCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<AssignJobCommand>
{
    public async Task Handle(AssignJobCommand request, CancellationToken cancellationToken)
    {
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken)
            ?? throw new NotFoundException(nameof(Job), request.JobId);

        // El Global Query Filter ya restringe esta comprobación al tenant activo: un
        // TenantUserId de otro tenant simplemente no se encuentra (ADR-002).
        var tenantUserExists = await dbContext.TenantUsers.AnyAsync(tu => tu.Id == request.TenantUserId, cancellationToken);
        if (!tenantUserExists)
        {
            throw new NotFoundException(nameof(TenantUser), request.TenantUserId);
        }

        job.AssignTo(request.TenantUserId);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

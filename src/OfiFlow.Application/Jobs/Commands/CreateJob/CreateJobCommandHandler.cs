using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Jobs.Commands.CreateJob;

public sealed class CreateJobCommandHandler(IApplicationDbContext dbContext, ITenantContext tenantContext)
    : IRequestHandler<CreateJobCommand, Guid>
{
    public async Task<Guid> Handle(CreateJobCommand request, CancellationToken cancellationToken)
    {
        var customerExists = await dbContext.Customers.AnyAsync(c => c.Id == request.CustomerId, cancellationToken);
        if (!customerExists)
        {
            throw new NotFoundException(CustomerErrors.NotFound, request.CustomerId);
        }

        var job = Job.Create(tenantContext.TenantId, request.CustomerId, request.Title, request.Description, request.Priority);

        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        return job.Id;
    }
}

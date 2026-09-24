using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Customers.Commands.DeleteCustomer;

public sealed class DeleteCustomerCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<DeleteCustomerCommand>
{
    public async Task Handle(DeleteCustomerCommand request, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(CustomerErrors.NotFound, request.Id);

        // Cierra el backlog abierto en specs/001-customer/spec.md: ahora que Job existe,
        // esta regla tiene algo real que comprobar (spec 003).
        var hasActiveJobs = await dbContext.Jobs.AnyAsync(
            j => j.CustomerId == request.Id && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled,
            cancellationToken);

        if (hasActiveJobs)
        {
            throw new BusinessRuleException(CustomerErrors.HasActiveJobs);
        }

        dbContext.Customers.Remove(customer);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

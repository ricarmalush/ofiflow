using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Quotes;

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

        // Spec 023 R13: un presupuesto es un documento comercial y no se borra a espaldas de nadie. Cualquiera, en cualquier estado, impide eliminar al cliente.
        var hasQuotes = await dbContext.Quotes.AnyAsync(q => q.CustomerId == request.Id, cancellationToken);

        if (hasQuotes)
        {
            throw new BusinessRuleException(CustomerErrors.HasQuotes);
        }

        dbContext.Customers.Remove(customer);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

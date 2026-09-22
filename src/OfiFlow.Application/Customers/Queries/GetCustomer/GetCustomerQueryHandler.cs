using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Customers.Queries.GetCustomer;

public sealed class GetCustomerQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetCustomerQuery, CustomerDto?>
{
    public Task<CustomerDto?> Handle(GetCustomerQuery request, CancellationToken cancellationToken)
    {
        return dbContext.Customers
            .Where(c => c.Id == request.Id)
            .Select(c => new CustomerDto(
                c.Id,
                c.Type.ToString(),
                c.Name,
                c.Email != null ? c.Email.Value : null,
                c.Phone != null ? c.Phone.Value : null,
                c.Address,
                c.Notes))
            .FirstOrDefaultAsync(cancellationToken);
    }
}

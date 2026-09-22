using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Customers.Queries.GetCustomers;

public sealed class GetCustomersQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetCustomersQuery, List<CustomerDto>>
{
    public Task<List<CustomerDto>> Handle(GetCustomersQuery request, CancellationToken cancellationToken)
    {
        return dbContext.Customers
            .OrderBy(c => c.Name)
            .Select(c => new CustomerDto(
                c.Id,
                c.Type.ToString(),
                c.Name,
                c.Email != null ? c.Email.Value : null,
                c.Phone != null ? c.Phone.Value : null,
                c.Address,
                c.Notes))
            .ToListAsync(cancellationToken);
    }
}

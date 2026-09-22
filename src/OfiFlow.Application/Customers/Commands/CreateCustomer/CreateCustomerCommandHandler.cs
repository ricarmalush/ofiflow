using MediatR;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Customers.Commands.CreateCustomer;

public sealed class CreateCustomerCommandHandler(IApplicationDbContext dbContext, ITenantContext tenantContext)
    : IRequestHandler<CreateCustomerCommand, Guid>
{
    public async Task<Guid> Handle(CreateCustomerCommand request, CancellationToken cancellationToken)
    {
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : Email.Create(request.Email);
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : PhoneNumber.Create(request.Phone);

        var customer = Customer.Create(
            tenantContext.TenantId,
            request.Type,
            request.Name,
            email,
            phone,
            request.Address,
            request.Notes);

        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync(cancellationToken);

        return customer.Id;
    }
}

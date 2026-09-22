using MediatR;

namespace OfiFlow.Application.Customers.Queries.GetCustomer;

public sealed record GetCustomerQuery(Guid Id) : IRequest<CustomerDto?>;

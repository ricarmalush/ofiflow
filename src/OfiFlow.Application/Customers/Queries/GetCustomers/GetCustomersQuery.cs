using MediatR;

namespace OfiFlow.Application.Customers.Queries.GetCustomers;

public sealed record GetCustomersQuery : IRequest<List<CustomerDto>>;

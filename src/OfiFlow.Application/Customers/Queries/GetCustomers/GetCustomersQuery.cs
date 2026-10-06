using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Customers.Queries.GetCustomers;

[RequiresPermission(Permission.CustomersRead)]
public sealed record GetCustomersQuery : IRequest<List<CustomerDto>>;

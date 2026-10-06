using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Customers.Queries.GetCustomer;

[RequiresPermission(Permission.CustomersRead)]
public sealed record GetCustomerQuery(Guid Id) : IRequest<CustomerDto?>;

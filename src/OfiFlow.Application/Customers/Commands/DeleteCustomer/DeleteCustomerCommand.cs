using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Customers.Commands.DeleteCustomer;

[RequiresPermission(Permission.CustomersDelete)]
public sealed record DeleteCustomerCommand(Guid Id) : IRequest;

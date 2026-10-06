using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Customers.Commands.CreateCustomer;

[RequiresPermission(Permission.CustomersWrite)]
public sealed record CreateCustomerCommand(
    CustomerType Type,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    string? Notes) : IRequest<Guid>;

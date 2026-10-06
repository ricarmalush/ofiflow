using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Customers.Commands.UpdateCustomer;

[RequiresPermission(Permission.CustomersWrite)]
public sealed record UpdateCustomerCommand(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    string? Notes) : IRequest;

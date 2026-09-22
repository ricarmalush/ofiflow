using MediatR;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Customers.Commands.CreateCustomer;

public sealed record CreateCustomerCommand(
    CustomerType Type,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    string? Notes) : IRequest<Guid>;

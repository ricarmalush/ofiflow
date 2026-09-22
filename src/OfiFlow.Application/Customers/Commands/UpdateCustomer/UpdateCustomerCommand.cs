using MediatR;

namespace OfiFlow.Application.Customers.Commands.UpdateCustomer;

public sealed record UpdateCustomerCommand(
    Guid Id,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    string? Notes) : IRequest;

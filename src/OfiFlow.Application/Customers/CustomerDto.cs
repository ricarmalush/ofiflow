namespace OfiFlow.Application.Customers;

public sealed record CustomerDto(
    Guid Id,
    string Type,
    string Name,
    string? Email,
    string? Phone,
    string? Address,
    string? Notes);

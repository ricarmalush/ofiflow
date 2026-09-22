namespace OfiFlow.Application.Jobs;

public sealed record JobDto(
    Guid Id,
    Guid CustomerId,
    string Title,
    string? Description,
    string Status,
    string Priority,
    DateTime? ScheduledDate,
    Guid? AssignedTenantUserId,
    int? EstimatedDurationMinutes);

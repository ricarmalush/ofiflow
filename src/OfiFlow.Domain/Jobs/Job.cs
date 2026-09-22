using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Jobs;

public sealed class Job : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid TenantId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public JobStatus Status { get; private set; }

    public JobPriority Priority { get; private set; }

    public DateTime? ScheduledDate { get; private set; }

    public Guid? AssignedTenantUserId { get; private set; }

    public int? EstimatedDurationMinutes { get; private set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private Job()
    {
        // Requerido por EF Core.
    }

    private Job(Guid id, Guid tenantId, Guid customerId, string title, string? description, JobPriority priority)
        : base(id)
    {
        TenantId = tenantId;
        CustomerId = customerId;
        Title = title;
        Description = description;
        Priority = priority;
        Status = JobStatus.New;
    }

    public static Job Create(Guid tenantId, Guid customerId, string title, string? description, JobPriority priority)
    {
        ValidateTitle(title);

        return new Job(Guid.NewGuid(), tenantId, customerId, title.Trim(), description, priority);
    }

    public void UpdateDetails(string title, string? description, JobPriority priority)
    {
        ValidateTitle(title);

        Title = title.Trim();
        Description = description;
        Priority = priority;
    }

    public void Start()
    {
        if (Status != JobStatus.New)
        {
            throw new InvalidOperationException($"No se puede iniciar un Job en estado {Status}.");
        }

        Status = JobStatus.InProgress;
    }

    public void Complete()
    {
        if (Status != JobStatus.InProgress)
        {
            throw new InvalidOperationException($"No se puede completar un Job en estado {Status}.");
        }

        Status = JobStatus.Completed;
    }

    public void Cancel()
    {
        if (Status == JobStatus.Completed)
        {
            throw new InvalidOperationException("No se puede cancelar un Job ya completado.");
        }

        Status = JobStatus.Cancelled;
    }

    public void AssignTo(Guid tenantUserId) => AssignedTenantUserId = tenantUserId;

    public void Unassign() => AssignedTenantUserId = null;

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("El título del trabajo es obligatorio.", nameof(title));
        }
    }
}

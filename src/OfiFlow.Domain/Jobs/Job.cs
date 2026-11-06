using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Jobs;

public sealed class Job : AggregateRoot, ITenantOwned, IAuditable
{
    // Longitudes máximas: fuente única para EF Core y los Validators (ADR-009 R3).
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    // Cotas de la programación (spec 020 R5 y R6): fuente única para el dominio y el validador.
    // La fecha es un instante UTC; el intervalo es [Min, Max).
    public static readonly DateTime ScheduledDateMin = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime ScheduledDateMax = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public const int EstimatedDurationMinutesMin = 1;
    public const int EstimatedDurationMinutesMax = 1440;

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

    /// <summary>
    /// Pone fecha (y, si se sabe, duración) al trabajo y lo deja <c>Scheduled</c> (spec 020 R1 a R3). Sirve para programar un
    /// trabajo nuevo y para reprogramar uno ya programado; <b>sustituye</b> la programación entera, así que una duración omitida
    /// borra la anterior. Los argumentos ya los validó Application: aquí un valor fuera de rango es un error de programación y
    /// lanza <see cref="ArgumentOutOfRangeException"/>, no un código de negocio.
    /// </summary>
    public void Schedule(DateTime scheduledDateUtc, int? estimatedDurationMinutes)
    {
        if (Status is not (JobStatus.New or JobStatus.Scheduled))
        {
            throw new DomainException(JobErrors.CannotSchedule, Status);
        }

        ArgumentOutOfRangeException.ThrowIfNotEqual(scheduledDateUtc.Kind, DateTimeKind.Utc, nameof(scheduledDateUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(scheduledDateUtc, ScheduledDateMin);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(scheduledDateUtc, ScheduledDateMax);

        if (estimatedDurationMinutes is { } minutes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(minutes, EstimatedDurationMinutesMin, nameof(estimatedDurationMinutes));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, EstimatedDurationMinutesMax, nameof(estimatedDurationMinutes));
        }

        ScheduledDate = scheduledDateUtc;
        EstimatedDurationMinutes = estimatedDurationMinutes;
        Status = JobStatus.Scheduled;
    }

    /// <summary>Quita el trabajo de la agenda: vuelve a <c>New</c> y se borran fecha y duración (spec 020 R7).</summary>
    public void Unschedule()
    {
        if (Status != JobStatus.Scheduled)
        {
            throw new DomainException(JobErrors.CannotUnschedule, Status);
        }

        ScheduledDate = null;
        EstimatedDurationMinutes = null;
        Status = JobStatus.New;
    }

    /// <summary>Un trabajo se inicia desde <c>New</c> o desde <c>Scheduled</c>; en este segundo caso conserva su fecha (spec 020 R8).</summary>
    public void Start()
    {
        if (Status is not (JobStatus.New or JobStatus.Scheduled))
        {
            throw new DomainException(JobErrors.CannotStart, Status);
        }

        Status = JobStatus.InProgress;
    }

    public void Complete()
    {
        if (Status != JobStatus.InProgress)
        {
            throw new DomainException(JobErrors.CannotComplete, Status);
        }

        Status = JobStatus.Completed;
    }

    public void Cancel()
    {
        if (Status == JobStatus.Completed)
        {
            throw new DomainException(JobErrors.CannotCancelCompleted);
        }

        Status = JobStatus.Cancelled;
    }

    public void AssignTo(Guid tenantUserId) => AssignedTenantUserId = tenantUserId;

    public void Unassign() => AssignedTenantUserId = null;

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException(JobErrors.TitleRequired);
        }
    }
}

namespace OfiFlow.Domain.Jobs;

public enum JobStatus
{
    New,
    Pending,
    Scheduled,
    InProgress,
    Completed,
    Cancelled
}

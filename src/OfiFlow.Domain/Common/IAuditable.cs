namespace OfiFlow.Domain.Common;

/// <summary>
/// Implemented by every entity whose CreatedAt/UpdatedAt are filled automatically by
/// Infrastructure's SaveChanges interceptor, not set by hand in Command Handlers — see ADR-006.
/// </summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }

    DateTime? UpdatedAt { get; set; }
}

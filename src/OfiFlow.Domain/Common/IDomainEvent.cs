using MediatR;

namespace OfiFlow.Domain.Common;

/// <summary>
/// Marker for something that happened in the Domain. Extends MediatR's INotification
/// (via the dependency-free MediatR.Contracts package) so it can be published as-is
/// by the SaveChanges interceptor, per ADR-006.
/// </summary>
public interface IDomainEvent : INotification;

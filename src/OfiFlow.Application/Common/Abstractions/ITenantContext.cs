namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// The tenant the current authenticated user is operating in, resolved from the JWT — see ADR-002.
/// Implemented in Infrastructure; Commands/Queries never accept a TenantId as raw input.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
}

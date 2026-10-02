namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// Thrown when IIdentityService fails to create a user (e.g. email already registered).
/// Carries error codes, not texts (ADR-011).
/// </summary>
public sealed class IdentityOperationException(IEnumerable<string> codes) : Exception(string.Join("; ", codes))
{
    public IReadOnlyList<string> Codes { get; } = codes.ToList();
}

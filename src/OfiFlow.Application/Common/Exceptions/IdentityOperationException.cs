namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// Thrown when IIdentityService fails to create a user (e.g. email already registered).
/// </summary>
public sealed class IdentityOperationException(IEnumerable<string> errors)
    : Exception(string.Join("; ", errors));

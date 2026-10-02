namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// Thrown when a Command/Query targets an entity that doesn't exist for the current tenant.
/// A resource of another tenant is reported exactly like a missing one (same code), so the
/// response never reveals that it exists elsewhere (ADR-002). Carries a code, not a text (ADR-011).
/// </summary>
public sealed class NotFoundException(string code, object key) : Exception($"{code} ({key})")
{
    public string Code { get; } = code;

    /// <summary>Id buscado: para diagnóstico en logs; no forma parte del mensaje al usuario.</summary>
    public object Key { get; } = key;
}

namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// A business rule that spans several aggregates was violated (e.g. "no se puede borrar un
/// Customer con Jobs activos") — distinct from NotFoundException (missing resource) and
/// FluentValidation's ValidationException (malformed input). Rules inside a single aggregate
/// are raised by Domain as DomainException. Carries a code, not a text (ADR-011).
/// </summary>
public sealed class BusinessRuleException(string code, params object?[] arguments) : Exception(code)
{
    public string Code { get; } = code;

    public IReadOnlyList<object?> Arguments { get; } = arguments;
}

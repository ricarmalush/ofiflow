namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// A business rule was violated (invalid state transition, cross-aggregate constraint like
/// "no se puede borrar un Customer con Jobs activos") — distinct from NotFoundException
/// (missing resource) and FluentValidation's ValidationException (malformed input).
/// </summary>
public sealed class BusinessRuleException(string message) : Exception(message);

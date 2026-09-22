namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// Thrown when a Command/Query targets an entity that doesn't exist for the current tenant.
/// The Api layer maps this to a 404 (not implemented yet — see specs/001-customer/tasks.md).
/// </summary>
public sealed class NotFoundException(string entityName, object key)
    : Exception($"'{entityName}' ({key}) no encontrado.");

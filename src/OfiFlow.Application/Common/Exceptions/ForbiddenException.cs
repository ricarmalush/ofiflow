namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// El usuario está autenticado pero no puede hacer esto: su rol no tiene el permiso que exige la operación (ADR-012 R5)
/// o su empresa está suspendida (spec 011 R15).
/// La API la convierte en un 403. Carries a code, not a text (ADR-011).
/// </summary>
public sealed class ForbiddenException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

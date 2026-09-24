namespace OfiFlow.Domain.Common;

/// <summary>
/// Error de negocio: una regla del dominio impide la operación (ADR-011).
/// Lleva un código estable (<c>customer.name_required</c>), no un texto: el mensaje para el usuario
/// lo pone la API desde su diccionario, en el idioma de la petición.
/// Se distingue a propósito de las excepciones genéricas de .NET, que indican un bug (500).
/// </summary>
/// <param name="code">Código estable del error; parte del contrato público de la API.</param>
/// <param name="arguments">Datos para el mensaje (estados, Ids). Nunca datos personales ni lo que escribió el usuario (ADR-009 R5).</param>
public sealed class DomainException(string code, params object?[] arguments) : Exception(code)
{
    public string Code { get; } = code;

    public IReadOnlyList<object?> Arguments { get; } = arguments;
}

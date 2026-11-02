namespace OfiFlow.Application.Common.Exceptions;

/// <summary>
/// Un servicio externo del que depende la operación falló (por ejemplo, el servidor de correo): la API funciona, pero no puede completarla. Se traduce a
/// <b>502</b> (spec 017 D4). Lleva un código estable (ADR-011) y <b>nunca</b> el motivo del servicio externo, que puede contener datos personales.
/// </summary>
public sealed class ExternalServiceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

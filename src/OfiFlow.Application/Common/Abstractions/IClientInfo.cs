namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Lo que se guarda del cliente que inicia una sesión (spec 016, R7 y R9): <b>desde qué navegador</b> y <b>desde qué red aproximada</b>. Sirve para que
/// su dueño reconozca sus sesiones en una lista («Chrome en Windows»), no para identificarle ni como prueba de nada: ambos datos los manda el cliente.
/// </summary>
/// <remarks>
/// Los valores ya vienen <b>saneados</b> por quien implementa esto (Infrastructure): la IP <b>truncada</b> (nunca completa) y el
/// <c>User-Agent</c> sin caracteres de control y recortado. Application nunca ve ni guarda nada más del cliente. Ambos son nulos si no hay
/// petición (por ejemplo, en una prueba) o si el cliente no los envía.
/// </remarks>
public interface IClientInfo
{
    /// <summary>
    /// La IP de origen <b>truncada</b>: una IPv4 sin su último octeto (<c>203.0.113.0</c>) y una IPv6 a sus 48 bits. Es un dato personal, y por eso se guarda
    /// solo así y solo mientras viva la fila (ADR-019). Las direcciones de loopback (desarrollo local) no son personales y se guardan tal cual.
    /// </summary>
    string? IpAddress { get; }

    /// <summary>El navegador tal como lo declara el cliente: sin caracteres de control y recortado a 256 caracteres. Es un dato, nunca se interpreta.</summary>
    string? UserAgent { get; }
}

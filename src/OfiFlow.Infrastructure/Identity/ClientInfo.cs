using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Lee el navegador y la red de la petición actual y los <b>sanea</b> antes de que lleguen a ningún sitio (spec 016, R7 y R9, ADR-019). Es la
/// única puerta de esos dos datos: Application y el resto de Infrastructure solo ven lo que sale de aquí.
/// </summary>
/// <remarks>
/// La IP sale de <c>RemoteIpAddress</c>, la misma fuente que el límite de peticiones: detrás de un proxy inverso valdrá lo mismo que valga allí
/// (hará falta <c>UseForwardedHeaders</c> con proxies de confianza, ADR-009), una sola vez para las dos cosas.
/// </remarks>
internal sealed class ClientInfo(IHttpContextAccessor httpContextAccessor) : IClientInfo
{
    /// <summary>El tope del navegador guardado (R7). La columna mide lo mismo.</summary>
    public const int MaxUserAgentLength = 256;

    /// <summary>El tope de una IP en texto (IPv6 completa: 45). La columna mide lo mismo.</summary>
    public const int MaxIpLength = 45;

    // Cuántos bytes de una IPv6 se conservan: 48 bits (un /48, lo que suele asignarse a un sitio), no el dispositivo.
    private const int Ipv6KeptBytes = 6;

    public string? IpAddress => TruncateIp(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);

    public string? UserAgent => CleanUserAgent(httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString());

    /// <summary>
    /// La IP truncada, o <c>null</c> si no hay. IPv4: el último octeto a cero. IPv6: solo los primeros 48 bits. Una IPv4 mapeada en IPv6
    /// (<c>::ffff:1.2.3.4</c>, lo habitual en un servidor de doble pila) se trata como IPv4. La de loopback no es un dato personal (es la
    /// propia máquina: desarrollo local) y se deja tal cual.
    /// </summary>
    internal static string? TruncateIp(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                bytes[3] = 0;
                break;

            case AddressFamily.InterNetworkV6:
                // Se pone a cero todo lo que sigue a los 48 bits. El identificador de zona (fe80::1%eth0) no viaja en los bytes: se pierde, y es lo que se quiere.
                Array.Clear(bytes, Ipv6KeptBytes, bytes.Length - Ipv6KeptBytes);
                break;

            default:
                return null;
        }

        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// El <c>User-Agent</c> saneado, o <c>null</c> si no hay nada útil. Quita los caracteres de control (un salto de línea no debe poder colarse en
    /// nada que lo muestre o lo registre) y recorta a <see cref="MaxUserAgentLength"/> sin partir una pareja de sustitución. No lo interpreta.
    /// </summary>
    internal static string? CleanUserAgent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = new StringBuilder(Math.Min(raw.Length, MaxUserAgentLength));
        foreach (var character in raw)
        {
            if (!char.IsControl(character))
            {
                cleaned.Append(character);
            }
        }

        var text = cleaned.ToString().Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= MaxUserAgentLength)
        {
            return text;
        }

        // Si el corte cae en medio de una pareja de sustitución (un emoji), se quita la mitad que queda suelta.
        var length = char.IsHighSurrogate(text[MaxUserAgentLength - 1]) ? MaxUserAgentLength - 1 : MaxUserAgentLength;
        return text[..length];
    }
}

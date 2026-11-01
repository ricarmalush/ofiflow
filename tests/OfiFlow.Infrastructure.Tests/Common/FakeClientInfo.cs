using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Infrastructure.Tests.Common;

/// <summary>
/// El cliente que inicia o renueva una sesión (spec 016), que el test controla. Las propiedades son <b>modificables</b> a propósito: un test cambia el
/// navegador o la red entre emitir y renovar para comprobar que la sesión conserva el dispositivo con el que empezó.
/// </summary>
public sealed class FakeClientInfo(string? ipAddress = null, string? userAgent = null) : IClientInfo
{
    public string? IpAddress { get; set; } = ipAddress;

    public string? UserAgent { get; set; } = userAgent;
}

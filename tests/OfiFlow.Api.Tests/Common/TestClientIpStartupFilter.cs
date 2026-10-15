using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// SOLO PARA TESTS. Permite que cada petición de prueba declare su IP de origen con la cabecera <c>X-Test-Client-Ip</c>.
/// El límite de peticiones se cuenta por IP, y todos los clientes de prueba comparten la misma: sin esto, un test que
/// necesita muchas aceptaciones de invitación (10 por hora y por IP) agotaría el límite sin que el límite tenga la culpa.
/// La API de producción no tiene este filtro, así que ignora esa cabecera (por eso <c>UseForwardedHeaders</c> es una
/// decisión pendiente aparte: ADR-009).
/// </summary>
public sealed class TestClientIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value) && IPAddress.TryParse(value.ToString(), out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            await pipeline();
        });

        next(app);
    };

    /// <summary>Una IP de prueba distinta cada vez (rango 10.x.y.z, privado).</summary>
    public static string NewIp()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3);
        return $"10.{bytes[0]}.{bytes[1]}.{bytes[2]}";
    }
}

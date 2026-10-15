using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// Arranca la API real en memoria. El secreto JWT se genera en cada ejecución: los tests no
/// dependen de los user-secrets de la máquina y funcionan igual en CI (ADR-009 R8).
/// Los tests que la usan no necesitan SQL Server: o no tocan la BD, o la validación rechaza
/// la petición antes de llegar al Handler.
/// </summary>
public sealed class OfiFlowApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:Secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Mfa:EncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        // Solo en tests: cada petición puede declarar su IP (ver TestClientIpStartupFilter). Sin la cabecera no cambia nada.
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestClientIpStartupFilter>());
    }
}

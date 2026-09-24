using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
    }
}

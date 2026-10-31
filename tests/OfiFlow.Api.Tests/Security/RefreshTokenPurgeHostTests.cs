using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El cableado de la purga de refresh tokens (spec 016) en el servidor de verdad: que la API registra el servicio en segundo plano y que su
/// configuración se lee. El comportamiento de la purga lo prueban los tests de Infrastructure contra SQL Server real; aquí solo se protege lo
/// que ninguno de ellos ve: si alguien borrara la línea de <c>Program.cs</c> que lo añade, la tabla volvería a crecer sin límite y no fallaría nada.
/// </summary>
public class RefreshTokenPurgeHostTests(OfiFlowApiFactory factory) : IClassFixture<OfiFlowApiFactory>
{
    [Fact]
    public void TheApiHost_RegistersThePurgeAsABackgroundService()
    {
        var hosted = factory.Services.GetServices<IHostedService>();

        Assert.Contains(hosted, service => service.GetType().Name == "RefreshTokenPurgeService");
    }

    [Fact]
    public void TheApiHost_ReadsItsConfiguration_AndTheTestsTurnItOff()
    {
        // Los tests de la API la desactivan por configuración (UseSetting en las factorías): comprobarlo prueba que la sección se lee de verdad
        // y que el resto de valores son los aprobados (D2: 7 días, lotes de 1000).
        var options = factory.Services.GetRequiredService<IOptions<RefreshTokenPurgeOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal(7, options.RetentionDays);
        Assert.Equal(1000, options.BatchSize);
        Assert.Equal(TimeSpan.FromHours(6), options.Interval);
    }
}

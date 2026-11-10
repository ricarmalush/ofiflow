using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Documents;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>El generador del PDF se registra con una sola línea y como una única instancia (spec 025): no guarda estado, así que se comparte entre peticiones.</summary>
public class QuotePdfRegistrationTests
{
    [Fact]
    public void TheRendererIsRegisteredAsASingletonBehindItsPort()
    {
        using var provider = new ServiceCollection().AddQuotePdf().BuildServiceProvider(validateScopes: true);

        var first = provider.GetRequiredService<IQuotePdfRenderer>();
        using var scope = provider.CreateScope();
        var second = scope.ServiceProvider.GetRequiredService<IQuotePdfRenderer>();

        Assert.IsType<PdfSharpQuotePdfRenderer>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void TheRegisteredRendererProducesAPdf()
    {
        using var provider = new ServiceCollection().AddQuotePdf().BuildServiceProvider();

        var bytes = provider.GetRequiredService<IQuotePdfRenderer>().Render(PdfTestData.Model());

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePdfRegistrationDoesNotNeedAnyConfiguration()
    {
        // A diferencia del correo, no hay opciones que validar al arrancar: construir el proveedor y pedir el generador basta.
        var services = new ServiceCollection();

        Assert.Same(services, services.AddQuotePdf());

        // El generador y la comprobación de arranque: nada más, y ninguna opción que configurar.
        Assert.Equal(2, services.Count);
        Assert.Contains(services, s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && s.ImplementationType == typeof(QuotePdfStartupCheck));
    }
}

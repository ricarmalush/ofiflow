using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Documents;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>
/// La comprobación de arranque del PDF (spec 025 D12): un servidor al que le falta la zona horaria de Madrid, o en el que el generador no puede producir un documento, no arranca. Es mejor que
/// arrancar y responder <c>500</c> a cada descarga sin que nadie sepa por qué.
/// </summary>
public class QuotePdfStartupCheckTests
{
    [Fact]
    public async Task OnThisMachineTheCheckPasses()
    {
        await new QuotePdfStartupCheck(new PdfSharpQuotePdfRenderer()).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AMissingTimeZoneStopsTheStartWithAMessageThatSaysHowToFixIt()
    {
        var check = new QuotePdfStartupCheck("Mundo/Inexistente", new PdfSharpQuotePdfRenderer());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

        Assert.Contains("Mundo/Inexistente", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tzdata", exception.Message, StringComparison.Ordinal);
        Assert.IsType<TimeZoneNotFoundException>(exception.InnerException);
    }

    [Fact]
    public async Task TheCheckUsesTheSameZoneAsTheDocumentDate()
    {
        // Si alguien cambiara la zona del documento, la comprobación seguiría vigilando la antigua: las dos tienen que ser la misma constante.
        Assert.Equal("Europe/Madrid", OfiFlow.Application.Quotes.Pdf.QuotePdfModelFactory.TimeZoneId);
        await new QuotePdfStartupCheck(QuotePdfModelFactory.TimeZoneId, new PdfSharpQuotePdfRenderer()).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARealHostStartsWithTheCheckRegistered()
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddQuotePdf()).Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task ARealHostDoesNotStartIfTheCheckFails()
    {
        // Control del cableado: el fallo de la comprobación llega hasta el arranque del anfitrión (no se pierde en una tarea de fondo).
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddHostedService(_ => new QuotePdfStartupCheck("Mundo/Inexistente", new PdfSharpQuotePdfRenderer())))
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    private sealed class ScriptedRenderer(Func<QuotePdfModel, byte[]> render) : IQuotePdfRenderer
    {
        public int Calls { get; private set; }

        public QuotePdfModel? Received { get; private set; }

        public byte[] Render(QuotePdfModel model)
        {
            Calls++;
            Received = model;
            return render(model);
        }
    }

    [Fact]
    public async Task TheCheckReallyGeneratesADocumentWithTheRendererItWasGiven()
    {
        var renderer = new ScriptedRenderer(_ => [1]);

        await new QuotePdfStartupCheck(renderer).StartAsync(CancellationToken.None);

        Assert.Equal(1, renderer.Calls);
        // Datos de mentira: ni de una empresa ni de una persona.
        Assert.Equal("Comprobación S.L.", renderer.Received!.Issuer.LegalName);
        Assert.DoesNotContain("@", renderer.Received.Issuer.LegalName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGeneratorThatFailsStopsTheStart()
    {
        var renderer = new ScriptedRenderer(_ => throw new InvalidOperationException("fallo de la librería"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuotePdfStartupCheck(renderer).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AGeneratorThatReturnsNothingStopsTheStart()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new QuotePdfStartupCheck(new ScriptedRenderer(_ => [])).StartAsync(CancellationToken.None));

        Assert.Contains("ningún documento", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingDoesNothing()
    {
        await new QuotePdfStartupCheck(new PdfSharpQuotePdfRenderer()).StopAsync(CancellationToken.None);
    }
}

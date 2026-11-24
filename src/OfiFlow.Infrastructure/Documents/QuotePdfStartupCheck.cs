using Microsoft.Extensions.Hosting;
using OfiFlow.Application.Quotes.Pdf;

namespace OfiFlow.Infrastructure.Documents;

/// <summary>
/// La comprobación de arranque del PDF (spec 025 D12): un servidor al que le falta algo que el PDF necesita <b>no arranca</b>, en vez de arrancar y responder <c>500</c> a cada descarga. Comprueba
/// la zona horaria de la fecha del documento (<c>Europe/Madrid</c>; en una imagen mínima de Linux hay que instalar los datos de zonas horarias), que las dos fuentes están dentro del ensamblado y que se
/// puede generar de verdad un documento pequeño con datos de mentira. No lee la base de datos ni ningún dato de una empresa.
/// </summary>
public sealed class QuotePdfStartupCheck : IHostedService
{
    private readonly string _timeZoneId;
    private readonly IQuotePdfRenderer _renderer;

    // El generador es el mismo que usará la API (el registrado en DI): la comprobación prueba el de verdad, no una copia.
    public QuotePdfStartupCheck(IQuotePdfRenderer renderer)
        : this(QuotePdfModelFactory.TimeZoneId, renderer)
    {
    }

    // Solo para los tests: una zona que no existe, o un generador que falla, demuestran que la comprobación falla en voz alta.
    internal QuotePdfStartupCheck(string timeZoneId, IQuotePdfRenderer renderer)
    {
        _timeZoneId = timeZoneId;
        _renderer = renderer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(_timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Falta la zona horaria {_timeZoneId} en este servidor y el PDF de los presupuestos la necesita para la fecha del documento. Instala los datos de zonas horarias (en Linux, el paquete tzdata).",
                exception);
        }

        // Las dos fuentes y una generación completa: si algo de la librería o de las fuentes no funciona aquí, se ve al arrancar.
        _ = PdfFonts.FontBytes(bold: false);
        _ = PdfFonts.FontBytes(bold: true);

        var model = new QuotePdfModel(
            new QuotePdfIssuer("Comprobación S.L.", "B00000000", "Calle 1", "28001", "Madrid", null, null, null),
            "P-0001",
            "Comprobación de arranque",
            new DateOnly(2026, 1, 1),
            null,
            IsDraft: true,
            new QuotePdfCustomer("Cliente", null),
            [new QuotePdfLine("Línea", 1m, 100, 21, 100)],
            [new QuotePdfVatLine(21, 100, 21)],
            100,
            21,
            121,
            null);

        if (_renderer.Render(model).Length == 0)
        {
            throw new InvalidOperationException("El generador de PDF no produjo ningún documento en la comprobación de arranque.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

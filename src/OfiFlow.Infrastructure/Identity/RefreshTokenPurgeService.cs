using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Logging;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El <b>primer servicio en segundo plano</b> de OfiFlow (spec 016, ADR-019): cada <see cref="RefreshTokenPurgeOptions.Interval"/> borra, por lotes,
/// los refresh tokens que caducaron hace más del margen. La tabla se consulta en cada petición (spec 015) y cada renovación añade una fila,
/// así que sin esto crecería sin límite.
/// </summary>
/// <remarks>
/// Garantías: <b>(R2)</b> nunca toca una sesión activa; <b>(R3)</b> borra en lotes con una pausa entre ellos; <b>(R4)</b> un ciclo que falla se registra y se
/// reintenta en el siguiente, y la API no se cae; no arranca de golpe (espera <see cref="RefreshTokenPurgeOptions.StartupDelay"/>) y se detiene limpiamente
/// con la API; <b>(R5)</b> deja un evento <c>1011</c> por ciclo que borró algo; <b>(R6)</b> es seguro con varias instancias a la vez, porque borrar
/// lo que otra instancia ya borró no hace nada. Vive en la API y no en <c>OfiFlow.Admin</c>: la consola de administración es interactiva y de un solo uso.
/// </remarks>
internal sealed partial class RefreshTokenPurgeService(
    IServiceScopeFactory scopeFactory,
    IOptions<RefreshTokenPurgeOptions> options,
    TimeProvider timeProvider,
    ILogger<RefreshTokenPurgeService> logger) : BackgroundService
{
    private readonly RefreshTokenPurgeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        try
        {
            await Task.Delay(_options.StartupDelay, timeProvider, stoppingToken);

            using var timer = new PeriodicTimer(_options.Interval, timeProvider);
            do
            {
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // R4: un ciclo que falla (la base de datos no responde, un bloqueo...) no puede tumbar la API ni parar el servicio.
                    // Se deja la excepción en el registro normal (no es un evento de seguridad) y se reintenta en el ciclo siguiente.
                    LogCycleFailed(logger, exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // La API se está deteniendo: salida limpia.
        }
    }

    /// <summary>Un ciclo entero: lotes hasta que no queda nada que borrar. Público para el test, que lo llama sin esperar al temporizador.</summary>
    /// <returns>Cuántas filas borró en total.</returns>
    internal async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        // R1: caducadas hace MÁS de RetentionDays. El corte lo calcula este ciclo con la hora de ahora; el borrado solo compara con él.
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(_options.RetentionDays);
        var total = 0;

        while (true)
        {
            // Un ámbito por lote: el contexto de base de datos no acumula rastreo ni conexiones entre lotes.
            int deleted;
            using (var scope = scopeFactory.CreateScope())
            {
                deleted = await scope.ServiceProvider.GetRequiredService<IRefreshTokenPurger>()
                    .PurgeBatchAsync(cutoff, _options.BatchSize, cancellationToken);
            }

            total += deleted;

            // Un lote incompleto significa que ya no queda nada por debajo del corte.
            if (deleted < _options.BatchSize)
            {
                break;
            }

            if (_options.PauseBetweenBatches > TimeSpan.Zero)
            {
                await Task.Delay(_options.PauseBetweenBatches, timeProvider, cancellationToken);
            }
        }

        if (total > 0)
        {
            // R5: un evento por ciclo que borró algo; un ciclo sin nada no deja ninguno.
            LogPurged(logger, total, cutoff);
        }

        return total;
    }

    [LoggerMessage(EventId = SecurityEventIds.RefreshTokensPurged, Level = LogLevel.Information,
        Message = "Purga de refresh tokens: {DeletedCount} filas borradas (caducadas antes de {Cutoff:O})")]
    private static partial void LogPurged(ILogger logger, int deletedCount, DateTime cutoff);

    // Sin EventId de seguridad: es mantenimiento. Un fallo aquí lo ve quien mire el registro, con su excepción.
    [LoggerMessage(Level = LogLevel.Error, Message = "Falló un ciclo de la purga de refresh tokens; se reintentará en el ciclo siguiente.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "La purga de refresh tokens está desactivada (RefreshTokenPurge:Enabled = false).")]
    private static partial void LogDisabled(ILogger logger);
}

using System.Diagnostics;

namespace OfiFlow.Api.Common;

/// <summary>
/// El suelo de tiempo de «he olvidado mi contraseña» (spec 018 R2, D2). <b>Se midió</b> y hizo falta: con la API real y SQL Server, la respuesta de una cuenta que existe tardaba
/// de mediana unos 17 ms y la de una que no existe, unos 5 ms, con una diferencia que una prueba estadística distingue sin dudar (t de Welch ≈ 54 con 400 muestras por serie). Esa
/// diferencia es el trabajo de crear el token (buscar, contar, anular, insertar, confirmar) y delata a la cuenta aunque la respuesta sea idéntica byte a byte.
/// </summary>
/// <remarks>
/// Cómo se oculta: la petición no termina antes de <see cref="ResponseFloor"/>, exista la cuenta o no, esté el correo apagado o no. Mientras el trabajo real quepa en el suelo, las dos
/// respuestas tardan lo mismo. Lo que <b>no</b> garantiza: si el trabajo de una cuenta que existe se pasara del suelo (una base de datos muy lenta), esa cola volvería a delatarla; por eso
/// el valor se puede subir por configuración y su razón de ser es mantenerlo muy por encima del p99 medido (≈ 34 ms).
/// </remarks>
public sealed class ForgotPasswordTimingOptions
{
    public const string SectionName = "ForgotPassword";

    public static readonly TimeSpan MaxFloor = TimeSpan.FromSeconds(5);

    /// <summary>Lo que como mínimo tarda la petición. Cero lo desactiva (los tests que no miden el tiempo lo usan para no esperar). 250 ms: unas 7 veces el p99 medido.</summary>
    public TimeSpan ResponseFloor { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Devuelve el motivo si la configuración no vale, o <c>null</c>. Nombra el ajuste y el rango; nunca refleja un valor.</summary>
    public string? Validate() =>
        ResponseFloor < TimeSpan.Zero || ResponseFloor > MaxFloor
            ? $"{SectionName}:ResponseFloor debe estar entre 0 y {MaxFloor.TotalSeconds:0} segundos (un valor mayor mantendría peticiones abiertas sin ganar nada)."
            : null;
}

/// <summary>Espera, si hace falta, lo que le falte a una petición para llegar al suelo de tiempo.</summary>
public static class ResponseTimeFloor
{
    /// <param name="startedTimestamp">El instante en que empezó el trabajo (<see cref="Stopwatch.GetTimestamp"/>).</param>
    /// <param name="floor">Lo mínimo que debe haber tardado en total.</param>
    public static async Task HoldAsync(long startedTimestamp, TimeSpan floor, CancellationToken cancellationToken)
    {
        var remaining = floor - Stopwatch.GetElapsedTime(startedTimestamp);

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }
}

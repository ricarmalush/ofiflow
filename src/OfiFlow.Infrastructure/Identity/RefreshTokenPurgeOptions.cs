namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// La configuración de la purga de refresh tokens caducados (spec 016, ADR-019). Todos tienen un valor por defecto razonable y la sección
/// <c>RefreshTokenPurge</c> puede faltar: a diferencia de un secreto, aquí un valor por defecto no es una debilidad. Sí se validan al arrancar
/// (un intervalo de cero haría girar el bucle sin parar, un margen de cero borraría tokens que aún pueden usarse).
/// </summary>
public sealed class RefreshTokenPurgeOptions
{
    public const string SectionName = "RefreshTokenPurge";

    /// <summary>Apagado general. Los tests de la API lo desactivan; en producción debe estar encendido.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cada cuánto se ejecuta un ciclo.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Cuánto espera antes del primer ciclo: para no competir con el arranque de la API ni con las migraciones. Son minutos y no cero a
    /// propósito (R4).
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// El margen (R1): se borran las filas cuya caducidad fue hace <b>más</b> de estos días. Es «el tiempo que el token pudo usarse más una
    /// semana para investigar un incidente» (D2).
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Cuántas filas como máximo borra cada lote (R3).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Pausa entre lotes, para no acaparar la tabla que se consulta en cada petición.</summary>
    public TimeSpan PauseBetweenBatches { get; set; } = TimeSpan.FromMilliseconds(200);

    public const int MaxRetentionDays = 3650;
    public const int MaxBatchSize = 10_000;

    /// <summary>Devuelve el motivo si la configuración no vale, o <c>null</c>. El mensaje dice qué ajuste y qué rango; nunca refleja datos de fuera.</summary>
    public string? Validate()
    {
        if (Interval <= TimeSpan.Zero)
        {
            return $"{SectionName}:Interval debe ser mayor que cero (un intervalo de cero haría girar el bucle sin descanso).";
        }

        if (StartupDelay < TimeSpan.Zero || PauseBetweenBatches < TimeSpan.Zero)
        {
            return $"{SectionName}:StartupDelay y {SectionName}:PauseBetweenBatches no pueden ser negativos.";
        }

        if (RetentionDays is < 1 or > MaxRetentionDays)
        {
            return $"{SectionName}:RetentionDays debe estar entre 1 y {MaxRetentionDays} (un margen de cero borraría tokens que aún pueden usarse).";
        }

        if (BatchSize is < 1 or > MaxBatchSize)
        {
            return $"{SectionName}:BatchSize debe estar entre 1 y {MaxBatchSize}.";
        }

        return null;
    }
}

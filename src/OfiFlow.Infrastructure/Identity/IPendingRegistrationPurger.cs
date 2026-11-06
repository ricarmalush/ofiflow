namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Lo que la purga en segundo plano (<see cref="RefreshTokenPurgeService"/>) necesita de los registros pendientes (spec 021 R18). Es interno e independiente de
/// <c>IPendingRegistrationService</c>: ningún caso de uso necesita borrar en lote, y la purga no necesita pedir ni canjear.
/// </summary>
internal interface IPendingRegistrationPurger
{
    /// <summary>
    /// Borra como mucho <paramref name="batchSize"/> pendientes caducados antes de <paramref name="cutoff"/>, los más antiguos primero. Devuelve cuántos borró; si es menos que el tamaño
    /// del lote, no queda nada más por debajo del corte.
    /// </summary>
    Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken);
}

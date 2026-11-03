namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El borrado de tokens de restablecimiento caducados (spec 018 R18). Es de Infrastructure y no de Application a propósito, como
/// <see cref="IRefreshTokenPurger"/>: ningún caso de uso lo necesita (lo llama el servicio en segundo plano de la purga).
/// Lo implementa <see cref="PasswordResetService"/>, que es el único fichero que lee la tabla.
/// </summary>
internal interface IPasswordResetTokenPurger
{
    /// <summary>
    /// Borra <b>un lote</b> de tokens cuya caducidad es anterior a <paramref name="cutoff"/>, estén o no usados, empezando por los más antiguos. Nunca toca
    /// uno que no caducó antes de la fecha de corte, y por tanto nunca un token vigente.
    /// </summary>
    /// <returns>Cuántas filas borró; si es menos que <paramref name="batchSize"/>, ya no queda nada que borrar.</returns>
    Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken);
}

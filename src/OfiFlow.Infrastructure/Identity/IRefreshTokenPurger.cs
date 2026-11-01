namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El borrado de refresh tokens caducados (spec 016). Es de Infrastructure y no de Application a propósito: ningún caso de uso lo
/// necesita (lo llama el servicio en segundo plano), y así el contrato que ve Application, <c>ITokenService</c>, no crece con algo que no usa.
/// Lo implementa <see cref="TokenService"/>, que sigue siendo el único fichero que lee la tabla de refresh tokens.
/// </summary>
internal interface IRefreshTokenPurger
{
    /// <summary>
    /// Borra <b>un lote</b> de refresh tokens cuya caducidad es anterior a <paramref name="cutoff"/>, estén o no revocados, empezando por los más
    /// antiguos. Nunca toca una fila que no caducó antes de la fecha de corte, y por tanto nunca una sesión activa.
    /// </summary>
    /// <returns>Cuántas filas borró; si es menos que <paramref name="batchSize"/>, ya no queda nada que borrar.</returns>
    Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken);
}

using System.Threading.RateLimiting;

namespace OfiFlow.Api.Common;

/// <summary>
/// El límite de presupuestos mandados por correo (spec 026, R18, D11): <b>20 por hora y por persona</b> que los manda. Mandar un correo a una dirección que elige quien lo manda, con un adjunto cuyo texto
/// también elige, desde el dominio de OfiFlow, es la vía clásica de abuso (<i>spam</i> y <i>phishing</i>): este es el límite que lo frena despacio, como el de las invitaciones (ADR-020 F1).
/// </summary>
/// <remarks>
/// Por qué no es una política más de <see cref="RateLimiting"/>: esas se evalúan <i>antes</i> de autenticar (ADR-017) y solo conocen la IP, y la clave aquí es la persona. La política por IP
/// (<see cref="RateLimiting.QuoteEmail"/>) frena un bucle de peticiones; esta, el goteo lento de una sesión robada. En memoria y por instancia, como el resto de límites (una sola instancia: ADR-009);
/// un valor inicial que se revisa con datos reales.
/// </remarks>
public sealed class QuoteEmailThrottle : IDisposable
{
    public const int PermitLimit = 20;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
        RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = Window,
            QueueLimit = 0
        }));

    /// <summary>Gasta un cupo de esta persona. <c>false</c> si ya agotó el de la hora; <paramref name="retryAfter"/> dice cuánto falta para el siguiente.</summary>
    public bool TryAcquire(Guid userId, out TimeSpan? retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(userId);

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : null;
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

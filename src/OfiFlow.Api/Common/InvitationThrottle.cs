using System.Threading.RateLimiting;

namespace OfiFlow.Api.Common;

/// <summary>
/// El límite de invitaciones (spec 017, R14, D10): <b>20 por hora y por persona</b> que invita. Enviar correo es la vía clásica de abuso (un <c>Admin</c> podría usar OfiFlow para llenar de
/// correo a un tercero revocando y volviendo a invitar), y el tope de 25 pendientes por empresa no lo frena porque revocar libera cupo.
/// </summary>
/// <remarks>
/// <b>Por qué no es una política más de <see cref="RateLimiting"/></b>: esas se evalúan <i>antes</i> de autenticar (ADR-017: CORS y límite van delante de la autenticación para que el <i>preflight</i> y los
/// 429 se contesten sin tocar la base de datos), así que solo conocen la IP. Aquí la clave es la persona, que solo se sabe una vez autenticada; por eso el endpoint lo consulta justo antes de
/// enviar la orden. Mover el limitador detrás de la autenticación habría cambiado el comportamiento de todos los demás endpoints para resolver el caso de uno.
/// En memoria y por instancia, como el resto de límites (una sola instancia: ADR-009); un valor inicial que se revisa con datos reales.
/// </remarks>
public sealed class InvitationThrottle : IDisposable
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

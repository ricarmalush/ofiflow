using System.Threading.RateLimiting;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// El límite de avisos «ya tienes una cuenta» por email (spec 021 R7): <b>3 por hora</b>, el mismo número que los registros pendientes (<see cref="PendingRegistration.MaxPerEmailPerHour"/>).
/// En memoria y por instancia, como <c>InvitationThrottle</c> (spec 017 D10) y el resto de límites: una sola instancia de la API (ADR-009). Reiniciar la API lo pone a cero, y es un riesgo aceptado
/// y pequeño: el límite por IP sigue activo y lo que se protege es un buzón, no un dato.
/// </summary>
/// <remarks>
/// La clave es una <b>huella</b> del email normalizado, no el email: así la memoria del proceso no guarda direcciones de gente que no es cliente. Los cubos inactivos los retira el propio limitador,
/// de modo que la memoria no crece con los emails distintos que se prueben (y cada uno cuesta además un cupo del límite por IP).
/// </remarks>
internal sealed class RegistrationNoticeThrottle : IRegistrationNoticeThrottle, IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(fingerprint =>
        RateLimitPartition.GetFixedWindowLimiter(fingerprint, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PendingRegistration.MaxPerEmailPerHour,
            Window = Window,
            QueueLimit = 0
        }));

    public bool TryAcquire(string email)
    {
        // La misma normalización que el resto del registro (ADR-004): «Ana@Example.com» y « ana@example.com » son el mismo email y comparten cupo.
        var fingerprint = OpaqueToken.Hash(email.Trim().ToUpperInvariant());

        using var lease = _limiter.AttemptAcquire(fingerprint);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

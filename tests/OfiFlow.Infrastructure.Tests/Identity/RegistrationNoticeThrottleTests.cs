using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El límite de avisos «ya tienes una cuenta» por email (spec 021 R7): 3 por hora, por email, en memoria. El reloj del limitador es el del sistema y no se puede adelantar, así que aquí se prueba el
/// reparto de cupos (cuántos, para quién y con qué normalización); que la ventana sea de una hora la comprueba el test de extremo a extremo con el <c>Retry-After</c> del límite de IP, y aquí el valor.
/// </summary>
public class RegistrationNoticeThrottleTests
{
    private static string Email(string prefix = "persona") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    [Fact]
    public void TheFirstThreeNotices_ForAnEmail_AreAllowed_AndTheFourthIsNot()
    {
        using var throttle = new RegistrationNoticeThrottle();
        var email = Email();

        Assert.True(throttle.TryAcquire(email));
        Assert.True(throttle.TryAcquire(email));
        Assert.True(throttle.TryAcquire(email));
        Assert.False(throttle.TryAcquire(email));
        Assert.False(throttle.TryAcquire(email));   // y sigue negado: no se «recupera» pidiendo más
    }

    [Fact]
    public void TheLimit_IsTheSameNumberAsThePendingRegistrations()
    {
        // Un solo número para el buzón de la víctima, venga el correo del camino que venga (R7).
        Assert.Equal(3, PendingRegistration.MaxPerEmailPerHour);
        using var throttle = new RegistrationNoticeThrottle();
        var email = Email();

        var allowed = Enumerable.Range(0, 10).Count(_ => throttle.TryAcquire(email));

        Assert.Equal(PendingRegistration.MaxPerEmailPerHour, allowed);
    }

    [Fact]
    public void TheWindow_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), RegistrationNoticeThrottle.Window);
    }

    [Fact]
    public void EachEmail_HasItsOwnSlots()
    {
        using var throttle = new RegistrationNoticeThrottle();
        var spent = Email("gastado");
        for (var i = 0; i < 5; i++)
        {
            throttle.TryAcquire(spent);
        }

        Assert.False(throttle.TryAcquire(spent));
        Assert.True(throttle.TryAcquire(Email("otro")));      // gastar el cupo de uno no gasta el de otro
    }

    [Theory]
    [InlineData("Ana.Perez@Example.com", "ana.perez@example.com")]
    [InlineData("ANA.PEREZ@EXAMPLE.COM", "ana.perez@example.com")]
    [InlineData("  ana.perez@example.com  ", "ana.perez@example.com")]
    public void TheSameEmailWrittenDifferently_SharesItsSlots(string first, string second)
    {
        // Si escribirlo en otra mayúscula diera cupos nuevos, el límite se saltaría con «Ana@», «ANA@», «aNa@»...
        using var throttle = new RegistrationNoticeThrottle();

        Assert.True(throttle.TryAcquire(first));
        Assert.True(throttle.TryAcquire(second));
        Assert.True(throttle.TryAcquire(first.ToUpperInvariant()));
        Assert.False(throttle.TryAcquire(second));
    }

    [Fact]
    public void ADifferentEmail_IsNeverTakenForTheSame()
    {
        // Control del test anterior: la normalización junta lo que es igual y NO junta lo que es distinto.
        using var throttle = new RegistrationNoticeThrottle();
        var a = "ana@example.com";
        var b = "ana@example.org";
        for (var i = 0; i < 3; i++)
        {
            throttle.TryAcquire(a);
        }

        Assert.False(throttle.TryAcquire(a));
        Assert.True(throttle.TryAcquire(b));
    }

    [Fact]
    public void TwoInstances_DoNotShareSlots_TheStateIsPerInstance()
    {
        // Es en memoria y por instancia (como el resto de límites; ADR-009): documentado como riesgo aceptado, y por eso en DI es un singleton.
        using var first = new RegistrationNoticeThrottle();
        using var second = new RegistrationNoticeThrottle();
        var email = Email();
        for (var i = 0; i < 3; i++)
        {
            first.TryAcquire(email);
        }

        Assert.False(first.TryAcquire(email));
        Assert.True(second.TryAcquire(email));
    }

    [Fact]
    public void ManyThreadsAtOnce_NeverGetMoreSlotsThanTheLimit()
    {
        using var throttle = new RegistrationNoticeThrottle();
        var email = Email();
        var allowed = 0;

        Parallel.For(0, 200, _ =>
        {
            if (throttle.TryAcquire(email))
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(PendingRegistration.MaxPerEmailPerHour, allowed);
    }
}

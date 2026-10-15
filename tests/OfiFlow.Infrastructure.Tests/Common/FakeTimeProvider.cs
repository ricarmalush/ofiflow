namespace OfiFlow.Infrastructure.Tests.Common;

/// <summary>Una hora que el test controla, para probar que un bloqueo de 15 minutos termina sin esperar 15 minutos.</summary>
public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTime _utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(_utcNow);

    public void Advance(TimeSpan time) => _utcNow += time;
}

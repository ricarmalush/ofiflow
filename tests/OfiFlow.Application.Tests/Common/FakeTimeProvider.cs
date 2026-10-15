namespace OfiFlow.Application.Tests.Common;

/// <summary>Una hora que el test controla, para probar la caducidad de las invitaciones sin esperar.</summary>
public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTime _utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(_utcNow);

    public void Advance(TimeSpan time) => _utcNow += time;
}

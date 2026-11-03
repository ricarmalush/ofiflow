using System.Diagnostics;
using OfiFlow.Api.Common;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El suelo de tiempo de «he olvidado mi contraseña» (spec 018 R2, D2). Que de verdad oculte la diferencia se MIDIÓ con la API real (ver la spec y el capítulo 9); aquí se fija
/// el mecanismo: que espere lo que falta, que no espere de más, y que su configuración se valide.
/// </summary>
public class ForgotPasswordTimingTests
{
    [Fact]
    public async Task HoldAsync_WaitsForWhatIsMissingToReachTheFloor()
    {
        var floor = TimeSpan.FromMilliseconds(300);
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(50); // el trabajo real

        await ResponseTimeFloor.HoldAsync(started, floor, CancellationToken.None);

        // Nunca antes del suelo (con una pequeña tolerancia del temporizador)...
        Assert.True(Stopwatch.GetElapsedTime(started) >= floor - TimeSpan.FromMilliseconds(15), $"Terminó antes del suelo: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
        // ...y tampoco mucho después: espera lo que falta, no el suelo entero otra vez.
        Assert.True(Stopwatch.GetElapsedTime(started) < floor + TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task HoldAsync_DoesNotWaitWhenTheWorkAlreadyTookLongerThanTheFloor()
    {
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(120);
        var before = Stopwatch.GetElapsedTime(started);

        await ResponseTimeFloor.HoldAsync(started, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(started) - before < TimeSpan.FromMilliseconds(40), "Esperó aunque ya había pasado el suelo.");
    }

    [Fact]
    public async Task HoldAsync_WithAZeroFloor_ReturnsAtOnce()
    {
        var started = Stopwatch.GetTimestamp();

        await ResponseTimeFloor.HoldAsync(started, TimeSpan.Zero, CancellationToken.None);

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task HoldAsync_StopsWaiting_WhenTheCallerGoesAway()
    {
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResponseTimeFloor.HoldAsync(Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(30), cancelled.Token));
    }

    // ===== La configuración =====

    [Fact]
    public void TheDefaultFloor_Is250Ms_TheApprovedValue_AndIsValid()
    {
        var options = new ForgotPasswordTimingOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(250), options.ResponseFloor);
        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:05")]
    [InlineData("00:00:00.300")]
    public void AFloorInRange_IsValid(string floor)
    {
        Assert.Null(new ForgotPasswordTimingOptions { ResponseFloor = TimeSpan.Parse(floor) }.Validate());
    }

    [Theory]
    [InlineData("-00:00:00.001")]
    [InlineData("00:00:05.001")]
    [InlineData("01:00:00")]
    public void AFloorOutOfRange_IsRejected_NamingTheSetting_AndNotTheValue(string floor)
    {
        var message = new ForgotPasswordTimingOptions { ResponseFloor = TimeSpan.Parse(floor) }.Validate();

        Assert.NotNull(message);
        Assert.Contains("ForgotPassword:ResponseFloor", message);
        Assert.DoesNotContain(floor, message);
    }
}

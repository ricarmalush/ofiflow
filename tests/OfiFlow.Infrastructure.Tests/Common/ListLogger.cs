using Microsoft.Extensions.Logging;

namespace OfiFlow.Infrastructure.Tests.Common;

/// <summary>
/// Logger en memoria para comprobar qué eventos de seguridad se registran y qué NO
/// contienen (ADR-009 R5), sin añadir un paquete de testing de logging.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}

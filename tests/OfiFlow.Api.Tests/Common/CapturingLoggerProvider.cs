using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// Guarda en memoria todo lo que se registra, para comprobar qué eventos de seguridad emite la API real
/// y qué NO contienen (ADR-009 R5), sin añadir un paquete de testing de logging.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    public sealed record LogEntry(string Category, LogLevel Level, int EventId, string Message);

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, formatter(state, exception)));
    }
}

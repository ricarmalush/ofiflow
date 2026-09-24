using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Logging;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// ADR-009 R5 y R6 en el peor caso, un 500: el cliente recibe un mensaje genérico (sin detalles
/// internos) con las cabeceras de seguridad y el traceId, y el log guarda la excepción completa.
/// </summary>
public class UnhandledExceptionTests : IClassFixture<UnhandledExceptionTests.ThrowingApiFactory>
{
    private const string InternalDetail = "Detalle interno: Server=sql-prod;Password=no-debe-salir";

    private readonly ThrowingApiFactory _factory;

    public UnhandledExceptionTests(ThrowingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnhandledException_Returns500WithoutInternalDetails_AndLogsIt()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "juan@example.com", password = "x" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(InternalDetail, body);
        Assert.Contains("Ha ocurrido un error inesperado.", body);
        Assert.Contains("traceId", body);

        // Las cabeceras sobreviven a que el ExceptionHandler limpie la respuesta.
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        var logged = Assert.Single(_factory.Logs, e => e.EventId.Id == SecurityEventIds.UnhandledException);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Equal(InternalDetail, logged.Exception?.Message);
    }

    public sealed class ThrowingApiFactory : WebApplicationFactory<Program>
    {
        public ConcurrentBag<(LogLevel Level, EventId EventId, Exception? Exception)> Logs { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:Secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
            builder.ConfigureTestServices(services => services.AddSingleton<ISender, ThrowingSender>());
        }
    }

    private sealed class ThrowingSender : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(InternalDetail);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new InvalidOperationException(InternalDetail);

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(InternalDetail);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLoggerProvider(ConcurrentBag<(LogLevel, EventId, Exception?)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentBag<(LogLevel, EventId, Exception?)> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Add((logLevel, eventId, exception));
        }
    }
}

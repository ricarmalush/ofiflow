using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Mail;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// El servicio que vacía la cola de correo (spec 018, R14, R15 y R16): entrega de uno en uno, un fallo no lo mata ni frena los siguientes, y lo que deja en el
/// registro es la categoría y nada más. Se prueba con un transporte guionizado, sin servidor de correo.
/// </summary>
public class EmailDispatchServiceTests
{
    private const string Address = "victima.secreta@example.com";
    private const string SecretToken = "tok_SECRETO_que_no_debe_salir_en_ningun_log";

    private sealed class ScriptedSender : IEmailSender
    {
        private readonly List<EmailMessage> _attempts = [];

        public bool IsEnabled { get; set; } = true;

        /// <summary>Qué hacer con cada envío, por orden. Sin guion, entrega.</summary>
        public Queue<Func<EmailMessage, Task>> Script { get; } = new();

        public IReadOnlyList<EmailMessage> Attempts
        {
            get
            {
                lock (_attempts)
                {
                    return [.. _attempts];
                }
            }
        }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            lock (_attempts)
            {
                _attempts.Add(message);
            }

            return Script.TryDequeue(out var step) ? step(message) : Task.CompletedTask;
        }
    }

    private static EmailMessage Message(string to = Address, string body = $"Abre https://app.example/auth/reset-password#token={SecretToken}") =>
        new(Email.Create(to), "Asunto fijo", body);

    private static (EmailDispatchService Service, EmailQueue Queue, ScriptedSender Sender, ListLogger<EmailDispatchService> Logger) Build(bool enabled = true)
    {
        var options = Options.Create(new EmailDispatchOptions { Enabled = enabled, QueueCapacity = 10 });
        var queue = new EmailQueue(options, new ListLogger<EmailQueue>());
        var sender = new ScriptedSender();
        var logger = new ListLogger<EmailDispatchService>();

        return (new EmailDispatchService(queue, sender, options, logger), queue, sender, logger);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < limit)
        {
            await Task.Delay(20);
        }
    }

    // ===== Entregar un mensaje (DeliverAsync) =====

    [Fact]
    public async Task ADeliveredMessage_GoesToTheTransportUntouched_AndLeavesNoWarning()
    {
        var (service, _, sender, logger) = Build();
        var message = Message();

        await service.DeliverAsync(message, CancellationToken.None);

        Assert.Same(message, Assert.Single(sender.Attempts));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(EmailFailureCategory.Timeout)]
    [InlineData(EmailFailureCategory.Authentication)]
    [InlineData(EmailFailureCategory.Rejected)]
    [InlineData(EmailFailureCategory.Unavailable)]
    [InlineData(EmailFailureCategory.Unknown)]
    public async Task ADeliveryFailure_IsAWarningWithTheCategory_AndNothingElse(EmailFailureCategory category)
    {
        var (service, _, sender, logger) = Build();
        sender.Script.Enqueue(_ => throw new EmailDeliveryException(category));

        await service.DeliverAsync(Message(), CancellationToken.None); // no lanza: la cola sigue

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.EmailDispatchFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(category.ToString(), entry.Message);
        var everything = string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("victima", everything);
        Assert.DoesNotContain(SecretToken, everything);
        Assert.DoesNotContain("reset-password", everything);
    }

    [Fact]
    public async Task AnUnexpectedError_IsLoggedByItsTypeOnly_NeverItsText_AndDoesNotPropagate()
    {
        var (service, _, sender, logger) = Build();
        sender.Script.Enqueue(_ => throw new InvalidOperationException($"el servidor dijo: no existe {Address} ni {SecretToken}"));

        await service.DeliverAsync(Message(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.EmailDispatchFailed);
        Assert.Contains("Unexpected", entry.Message);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        var everything = string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("victima", everything);
        Assert.DoesNotContain(SecretToken, everything);
        Assert.DoesNotContain("el servidor dijo", everything);
    }

    [Fact]
    public async Task WithTheTransportOff_TheMessageIsDroppedQuietly_AndNothingIsSent()
    {
        var (service, _, sender, logger) = Build();
        sender.IsEnabled = false;

        await service.DeliverAsync(Message(), CancellationToken.None);

        Assert.Empty(sender.Attempts);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task WhenTheCallerCancels_ItIsACancellation_NotALoggedFailure()
    {
        var (service, _, sender, logger) = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        sender.Script.Enqueue(_ => throw new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeliverAsync(Message(), cancelled.Token));

        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.EmailDispatchFailed);
    }

    // ===== El servicio entero =====

    [Fact]
    public async Task TheService_DeliversWhatIsQueued_InOrder_OneAtATime()
    {
        var (service, queue, sender, _) = Build();
        await service.StartAsync(CancellationToken.None);

        queue.TryEnqueue(Message("uno@example.com"));
        queue.TryEnqueue(Message("dos@example.com"));
        queue.TryEnqueue(Message("tres@example.com"));
        await WaitUntilAsync(() => sender.Attempts.Count >= 3);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["uno@example.com", "dos@example.com", "tres@example.com"], sender.Attempts.Select(m => m.To.Value));
    }

    [Fact]
    public async Task AFailureInTheMiddle_DoesNotKillTheService_NorHoldBackTheNextMessages()
    {
        var (service, queue, sender, logger) = Build();
        sender.Script.Enqueue(_ => Task.CompletedTask);                                                       // 1.º: bien
        sender.Script.Enqueue(_ => throw new EmailDeliveryException(EmailFailureCategory.Unavailable));       // 2.º: el servidor de correo está caído
        sender.Script.Enqueue(_ => throw new InvalidOperationException("algo inesperado"));                   // 3.º: un error cualquiera
        await service.StartAsync(CancellationToken.None);

        queue.TryEnqueue(Message("uno@example.com"));
        queue.TryEnqueue(Message("dos@example.com"));
        queue.TryEnqueue(Message("tres@example.com"));
        queue.TryEnqueue(Message("cuatro@example.com"));                                                        // 4.º: de nuevo bien
        await WaitUntilAsync(() => sender.Attempts.Count >= 4);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(4, sender.Attempts.Count);                                                                 // los cuatro se intentaron
        Assert.Equal(2, logger.Entries.Count(e => e.EventId.Id == SecurityEventIds.EmailDispatchFailed));      // y solo los dos que fallaron dejaron evento
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StoppingWhileIdle_IsClean()
    {
        var (service, queue, sender, _) = Build();
        await service.StartAsync(CancellationToken.None);

        // Se espera a que el servicio YA esté leyendo (entregando un mensaje y quedándose inactivo): parar antes de que arranque no prueba nada de la parada limpia.
        queue.TryEnqueue(Message());
        await WaitUntilAsync(() => sender.Attempts.Count >= 1);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhatIsStillQueuedWhenItStops_IsLost_ByDesign()
    {
        // R15: sin persistencia. Un mensaje que nadie leyó antes de parar no se entrega después: ni queda en ningún sitio ni se reintenta.
        var (service, queue, sender, _) = Build();
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        queue.TryEnqueue(Message());
        await Task.Delay(200);

        Assert.Empty(sender.Attempts);
    }

    [Fact]
    public async Task WhenDisabled_TheServiceDoesNothing()
    {
        var (service, queue, sender, _) = Build(enabled: false);

        await service.StartAsync(CancellationToken.None);
        queue.TryEnqueue(Message()); // la cola apagada ya lo rechaza
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(sender.Attempts);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }
}

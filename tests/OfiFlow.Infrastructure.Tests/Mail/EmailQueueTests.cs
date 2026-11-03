using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Mail;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// La cola de correo en memoria (spec 018, R14 y R15, D3): acotada, sin bloquear nunca a quien encola, sin persistir y sin registrar el contenido de ningún
/// mensaje. Lo que se prueba es lo que la hace distinta de enviar dentro de la petición.
/// </summary>
public class EmailQueueTests
{
    private const string SecretToken = "tok_SECRETO_que_no_debe_salir_en_ningun_log";

    private static EmailMessage Message(string to = "persona@example.com", string body = "Hola") => new(Email.Create(to), "Asunto fijo", body);

    private static EmailQueue Queue(int capacity = 3, bool enabled = true, ListLogger<EmailQueue>? logger = null) =>
        new(Options.Create(new EmailDispatchOptions { QueueCapacity = capacity, Enabled = enabled }), logger ?? new ListLogger<EmailQueue>());

    // ===== Encolar y leer =====

    [Fact]
    public async Task AMessageThatIsQueued_IsReadBack_InTheSameOrder()
    {
        var queue = Queue();

        Assert.True(queue.TryEnqueue(Message("uno@example.com")));
        Assert.True(queue.TryEnqueue(Message("dos@example.com")));
        Assert.True(queue.TryEnqueue(Message("tres@example.com")));

        Assert.Equal("uno@example.com", (await queue.Reader.ReadAsync()).To.Value);
        Assert.Equal("dos@example.com", (await queue.Reader.ReadAsync()).To.Value);
        Assert.Equal("tres@example.com", (await queue.Reader.ReadAsync()).To.Value);
    }

    [Fact]
    public void ANullMessage_IsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => Queue().TryEnqueue(null!));
    }

    // ===== Llena: descarta, registra y no bloquea (R15) =====

    [Fact]
    public void WhenFull_TheNewMessageIsDiscarded_ItIsLogged_AndTheOnesAlreadyQueuedStay()
    {
        var logger = new ListLogger<EmailQueue>();
        var queue = Queue(capacity: 2, logger: logger);
        Assert.True(queue.TryEnqueue(Message("uno@example.com")));
        Assert.True(queue.TryEnqueue(Message("dos@example.com")));

        var accepted = queue.TryEnqueue(Message("tres@example.com"));

        Assert.False(accepted);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("llena", warning.Message);
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal("uno@example.com", first.To.Value);
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal("dos@example.com", second.To.Value);
        Assert.False(queue.Reader.TryRead(out _)); // el tercero no está
    }

    [Fact]
    public void WhenFull_TheLogCarriesNeitherTheAddressNorTheBodyOfTheDiscardedMessage()
    {
        var logger = new ListLogger<EmailQueue>();
        var queue = Queue(capacity: 1, logger: logger);
        queue.TryEnqueue(Message());

        queue.TryEnqueue(Message("victima@example.com", $"Abre https://app.example/auth/reset-password#token={SecretToken}"));

        var everything = string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("victima", everything);
        Assert.DoesNotContain(SecretToken, everything);
        Assert.DoesNotContain("reset-password", everything);
    }

    [Fact]
    public async Task WhenFull_ItDoesNotBlockTheCaller()
    {
        // El motivo de ser de la cola: quien encola no espera. Con la cola llena, TryEnqueue vuelve al instante (no espera un hueco).
        var queue = Queue(capacity: 1);
        queue.TryEnqueue(Message());

        var attempt = Task.Run(() => queue.TryEnqueue(Message("otro@example.com")));

        Assert.Same(attempt, await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.False(await attempt);
    }

    [Fact]
    public void AfterReadingOne_ThereIsRoomAgain()
    {
        var queue = Queue(capacity: 1);
        queue.TryEnqueue(Message("uno@example.com"));
        Assert.False(queue.TryEnqueue(Message("dos@example.com")));

        Assert.True(queue.Reader.TryRead(out _));

        Assert.True(queue.TryEnqueue(Message("dos@example.com")));
    }

    [Fact]
    public async Task WithManyWritersAtOnce_ExactlyTheCapacityIsAccepted_AndTheRestIsDiscarded()
    {
        var queue = Queue(capacity: 50);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => queue.TryEnqueue(Message($"p{i}@example.com")))));

        Assert.Equal(50, results.Count(accepted => accepted));
        Assert.Equal(150, results.Count(accepted => !accepted));
        var read = 0;
        while (queue.Reader.TryRead(out _))
        {
            read++;
        }

        Assert.Equal(50, read);
    }

    // ===== Apagada =====

    [Fact]
    public void WhenDisabled_NothingIsQueued_AndNothingIsLoggedAsAFullQueue()
    {
        var logger = new ListLogger<EmailQueue>();
        var queue = Queue(enabled: false, logger: logger);

        var accepted = queue.TryEnqueue(Message());

        Assert.False(accepted);
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Empty(logger.Entries);
    }

    // ===== Las opciones =====

    [Fact]
    public void TheDefaults_AreTheApprovedOnes_AndAreValid()
    {
        var options = new EmailDispatchOptions();

        Assert.True(options.Enabled);
        Assert.Equal(100, options.QueueCapacity);
        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void ACapacityOutOfRange_IsRejected_NamingTheSettingAndNotTheValue(int capacity)
    {
        var message = new EmailDispatchOptions { QueueCapacity = capacity }.Validate();

        Assert.NotNull(message);
        Assert.Contains("EmailDispatch:QueueCapacity", message);
        Assert.DoesNotContain(capacity.ToString(), message.Replace("10000", string.Empty).Replace("1 y", string.Empty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    public void TheLimitsOfTheRange_AreValid(int capacity)
    {
        Assert.Null(new EmailDispatchOptions { QueueCapacity = capacity }.Validate());
    }

    // ===== El registro =====

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void AddEmail_RegistersTheQueueOnce_SeenAsItsPortAndAsItsReaderSide()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmail(Config());
        using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IEmailQueue>();
        var concrete = provider.GetRequiredService<EmailQueue>();

        Assert.Same(concrete, port);                                         // una sola cola: lo que encola el caso de uso es lo que lee el servicio
        Assert.Same(port, provider.GetRequiredService<IEmailQueue>());       // y es un singleton
    }

    [Fact]
    public void AddEmail_RegistersTheDispatchServiceAsAHostedService()
    {
        var services = new ServiceCollection();
        services.AddEmail(Config());

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EmailDispatchService));
    }

    [Fact]
    public void AddEmail_WithAnInvalidDispatchSection_FailsWhenTheOptionsAreRead()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmail(Config(("EmailDispatch:QueueCapacity", "0")));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailDispatchOptions>>().Value);
    }

    [Fact]
    public async Task AnInvalidDispatchSection_StopsTheHostFromStarting()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EmailDispatch:QueueCapacity"] = "0" });
        builder.Services.AddLogging();
        builder.Services.AddEmail(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("EmailDispatch", exception.Message);
    }

    [Fact]
    public void TheAdminConsole_NeverGetsTheQueueNorItsService()
    {
        // La consola de administración es interactiva y de un solo uso: no recibe AddEmail, así que ni cola ni tarea en segundo plano.
        var services = new ServiceCollection();
        services.AddAdminInfrastructure(Config(
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Mfa:EncryptionKey", Convert.ToBase64String(new byte[32]))));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEmailQueue));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }
}

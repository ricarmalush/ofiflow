using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Infrastructure;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La purga de refresh tokens caducados (spec 016, bloque 1) contra SQL Server real: lo que se prueba es un borrado por lotes de verdad
/// (<c>ExecuteDelete</c> con <c>TOP</c>), que una base en memoria no puede ejecutar, y dos instancias que lo hacen a la vez. Cada caso de «se borra»
/// va con su control de «no se borra».
/// </summary>
public class RefreshTokenPurgeTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddDays(-7);

    private static readonly JwtOptions Jwt = new()
    {
        Secret = Convert.ToBase64String(new byte[32]),
        Issuer = "OfiFlow.Tests",
        Audience = "OfiFlow.Tests",
        AccessTokenMinutes = 30,
        RefreshTokenDays = 7
    };

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    /// <summary>Todos los tests de la clase comparten el contenedor, y la purga es global: cada uno empieza con la tabla vacía.</summary>
    private async Task ResetAsync()
    {
        await using var db = Open();
        await db.RefreshTokens.ExecuteDeleteAsync();
    }

    private static RefreshToken NewRow(DateTime expiresAt, bool revoked = false, Guid? userId = null, Guid? sessionId = null)
    {
        var row = new RefreshToken(userId ?? Guid.NewGuid(), Guid.NewGuid(), sessionId ?? Guid.NewGuid(), Guid.NewGuid().ToString("N"), expiresAt);
        if (revoked)
        {
            row.Revoke();
        }

        return row;
    }

    private async Task SeedAsync(params RefreshToken[] rows)
    {
        await using var db = Open();
        db.RefreshTokens.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync()
    {
        await using var db = Open();
        return await db.RefreshTokens.CountAsync();
    }

    private async Task<int> PurgeBatchAsync(int batchSize)
    {
        await using var db = Open();
        return await new TokenService(db, Options.Create(Jwt), new FakeClientInfo(), NullLogger<TokenService>.Instance).PurgeBatchAsync(Cutoff, batchSize, CancellationToken.None);
    }

    // ===== Un lote (TokenService.PurgeBatchAsync) =====

    [Fact]
    public async Task PurgeBatch_DeletesRowsExpiredBeforeTheCutoff_RevokedOrNot_OfAnyAccount()
    {
        await ResetAsync();
        await SeedAsync(
            NewRow(Cutoff.AddDays(-1)),                 // abandonada: caducó sin que nadie la revocara
            NewRow(Cutoff.AddDays(-1), revoked: true),  // rotada y caducada
            NewRow(Cutoff.AddMinutes(-1)));             // otra cuenta, por poco

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(3, deleted);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_KeepsEverythingThatIsNotExpiredBeforeTheCutoff()
    {
        await ResetAsync();
        await SeedAsync(
            NewRow(Now.AddDays(3)),                          // sesión activa
            NewRow(Now.AddDays(3), revoked: true),           // revocada pero aún sin caducar: sigue sirviendo para la detección de reutilización
            NewRow(Now.AddMinutes(-1)),                      // caducó hace un minuto
            NewRow(Cutoff.AddMinutes(1)),                    // caducó dentro del margen
            NewRow(Cutoff.AddMinutes(1), revoked: true));    // revocada y dentro del margen

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(0, deleted);
        Assert.Equal(5, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_TheCutoffItself_IsKept_SoTheMarginIsNeverShorterThanConfigured()
    {
        await ResetAsync();
        await SeedAsync(NewRow(Cutoff), NewRow(Cutoff.AddTicks(-10_000)));  // exactamente en el corte, y un instante antes

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(1, deleted); // solo la que caducó ANTES del corte
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_RespectsTheBatchSize_AndDeletesTheOldestFirst()
    {
        await ResetAsync();
        var oldest = NewRow(Cutoff.AddDays(-30));
        var middle = NewRow(Cutoff.AddDays(-20));
        var newest = NewRow(Cutoff.AddDays(-10));
        await SeedAsync(newest, oldest, middle);

        var deleted = await PurgeBatchAsync(2);

        Assert.Equal(2, deleted); // un lote incompleto avisaría de que ya no queda nada; uno completo, de que puede haber más
        await using var db = Open();
        var remaining = await db.RefreshTokens.SingleAsync();
        Assert.Equal(newest.Id, remaining.Id);
    }

    [Fact]
    public async Task PurgeBatch_WithNothingToDelete_ReturnsZero()
    {
        await ResetAsync();

        Assert.Equal(0, await PurgeBatchAsync(100));
    }

    [Fact]
    public async Task PurgeBatch_AnActiveSession_StaysActive_WhileItsOldRotationsAreRemoved()
    {
        // R2: una sesión que lleva semanas activa acumula filas viejas (una por renovación). Borrarlas no cambia nada: la fila activa no caducó.
        await ResetAsync();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await SeedAsync(
            NewRow(Cutoff.AddDays(-20), revoked: true, userId, sessionId),
            NewRow(Cutoff.AddDays(-12), revoked: true, userId, sessionId),
            NewRow(Cutoff.AddDays(-9), revoked: true, userId, sessionId),
            NewRow(DateTime.UtcNow.AddDays(5), revoked: false, userId, sessionId)); // la activa de verdad (IsSessionActive usa la hora real)

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(3, deleted);
        await using var db = Open();
        var service = new TokenService(db, Options.Create(Jwt), new FakeClientInfo(), NullLogger<TokenService>.Instance);
        Assert.True(await service.IsSessionActiveAsync(userId, sessionId, CancellationToken.None));
        Assert.Equal(1, await CountAsync());
    }

    private sealed class StatementRecorder(List<string> statements) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            statements.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task PurgeBatch_IsASingleSetBasedDelete_NotARowByRowLoad()
    {
        // El objetivo de D3: un DELETE por lote, no cargar las filas para borrarlas una a una (1000 sentencias) ni un DELETE sin tope.
        await ResetAsync();
        await SeedAsync([.. Enumerable.Range(0, 5).Select(i => NewRow(Cutoff.AddDays(-1 - i)))]);
        var statements = new List<string>();
        await using var db = _fixture.CreateDbContext(Guid.NewGuid(), builder => builder.AddInterceptors(new StatementRecorder(statements)));

        await new TokenService(db, Options.Create(Jwt), new FakeClientInfo(), NullLogger<TokenService>.Instance).PurgeBatchAsync(Cutoff, 3, CancellationToken.None);

        var sql = Assert.Single(statements);
        Assert.StartsWith("DELETE", sql);                                   // un DELETE, no un SELECT para cargar filas
        Assert.Contains("TOP(@p)", sql);                                    // con tope de lote, parametrizado
        Assert.Contains("ORDER BY [r0].[ExpiresAt]", sql);                  // lo más antiguo primero, por el índice de la caducidad
        Assert.Contains("[r0].[ExpiresAt] < @cutoff", sql);                 // solo lo caducado antes del corte
        Assert.DoesNotContain(Cutoff.ToString("yyyy"), sql);                // el corte viaja como parámetro, no pegado en el texto
    }

    // ===== Un ciclo entero (RefreshTokenPurgeService.RunCycleAsync) =====

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(_fixture.ConnectionString));
        services.AddSingleton<IOptions<JwtOptions>>(Options.Create(Jwt));
        services.AddSingleton<ILogger<TokenService>>(NullLogger<TokenService>.Instance);
        services.AddSingleton<IClientInfo>(new FakeClientInfo()); // TokenService lo necesita (spec 016, bloque 2); la purga no lo usa
        services.AddScoped<TokenService>();
        services.AddScoped<IRefreshTokenPurger>(provider => provider.GetRequiredService<TokenService>());

        return services.BuildServiceProvider();
    }

    private static RefreshTokenPurgeService NewService(
        IServiceProvider provider, RefreshTokenPurgeOptions options, ListLogger<RefreshTokenPurgeService>? logger = null, TimeProvider? time = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), time ?? new FakeTimeProvider(Now), logger ?? new ListLogger<RefreshTokenPurgeService>());

    private static RefreshTokenPurgeOptions SmallBatches(int batchSize = 3) => new() { BatchSize = batchSize, PauseBetweenBatches = TimeSpan.Zero };

    [Fact]
    public async Task RunCycle_DeletesEverythingBeyondTheMargin_InSeveralBatches()
    {
        await ResetAsync();
        await SeedAsync([.. Enumerable.Range(0, 10).Select(i => NewRow(Cutoff.AddDays(-1 - i), revoked: i % 2 == 0))]);
        await SeedAsync(NewRow(Now.AddDays(2)), NewRow(Cutoff.AddDays(1))); // las que se quedan
        using var provider = Provider();

        var total = await NewService(provider, SmallBatches(3)).RunCycleAsync(CancellationToken.None);

        Assert.Equal(10, total);   // 4 lotes: 3 + 3 + 3 + 1
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task RunCycle_ABatchExactlyAsBigAsTheBatchSize_DoesNotLoopForever()
    {
        // 6 filas con lotes de 3: el segundo lote viene completo, así que hay un tercero, vacío, que es el que termina el ciclo.
        await ResetAsync();
        await SeedAsync([.. Enumerable.Range(0, 6).Select(i => NewRow(Cutoff.AddDays(-1 - i)))]);
        using var provider = Provider();

        var total = await NewService(provider, SmallBatches(3)).RunCycleAsync(CancellationToken.None);

        Assert.Equal(6, total);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task RunCycle_TheMarginIsRetentionDays_CountedFromNow()
    {
        await ResetAsync();
        await SeedAsync(
            NewRow(Now.AddDays(-8)),                   // fuera del margen de 7 días
            NewRow(Now.AddDays(-7).AddMinutes(1)),     // dentro, por poco
            NewRow(Now.AddDays(-7)),                   // justo en el corte: se queda
            NewRow(Now.AddDays(-1)));
        using var provider = Provider();

        var total = await NewService(provider, SmallBatches(100)).RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task RunCycle_ALongerMargin_KeepsWhatAShorterOneWouldDelete()
    {
        await ResetAsync();
        await SeedAsync(NewRow(Now.AddDays(-10)), NewRow(Now.AddDays(-40)));
        using var provider = Provider();

        var total = await NewService(provider, new RefreshTokenPurgeOptions { RetentionDays = 30, BatchSize = 100, PauseBetweenBatches = TimeSpan.Zero })
            .RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, total); // solo la de hace 40 días
    }

    [Fact]
    public async Task RunCycle_LogsOneEventWithTheCount_AndNothingSensitive()
    {
        await ResetAsync();
        var rows = Enumerable.Range(0, 7).Select(i => NewRow(Cutoff.AddDays(-1 - i))).ToArray();
        await SeedAsync(rows);
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        await NewService(provider, SmallBatches(3), logger).RunCycleAsync(CancellationToken.None);

        // Un evento por CICLO (no por lote: 7 filas con lotes de 3 son tres lotes) y con el total.
        var entry = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokensPurged);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("7 filas borradas", entry.Message);
        Assert.Contains("2026-10-23", entry.Message); // el corte: ahora menos 7 días
        Assert.DoesNotContain(rows[0].Id.ToString(), entry.Message);
        Assert.DoesNotContain(rows[0].TokenHash, entry.Message);
    }

    [Fact]
    public async Task RunCycle_WithNothingToDelete_LeavesNoEvent()
    {
        await ResetAsync();
        await SeedAsync(NewRow(Now.AddDays(2)));
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        var total = await NewService(provider, SmallBatches(), logger).RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, total);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task RunCycle_TwoInstancesAtOnce_DeleteEverythingWithoutErrors()
    {
        // R6: con varias instancias de la API, cada una tiene su servicio. Borrar lo que otra ya borró no hace nada, ni falla.
        await ResetAsync();
        await SeedAsync([.. Enumerable.Range(0, 600).Select(i => NewRow(Cutoff.AddDays(-1 - (i % 20))))]);
        await SeedAsync(NewRow(Now.AddDays(2)));
        using var providerA = Provider();
        using var providerB = Provider();

        var totals = await Task.WhenAll(
            NewService(providerA, SmallBatches(50)).RunCycleAsync(CancellationToken.None),
            NewService(providerB, SmallBatches(50)).RunCycleAsync(CancellationToken.None));

        Assert.Equal(600, totals.Sum()); // cada fila la borra una sola de las dos
        Assert.Equal(1, await CountAsync());
    }

    // ===== El servicio en segundo plano (BackgroundService) =====

    private sealed class FlakyPurger : IRefreshTokenPurger
    {
        public int Calls;

        public Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken)
        {
            // La primera vez falla (la base de datos no responde); las siguientes, bien y sin nada que borrar.
            return Interlocked.Increment(ref Calls) == 1 ? throw new InvalidOperationException("la base de datos no responde") : Task.FromResult(0);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < limit)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Service_ACycleThatFails_DoesNotKillIt_AndTheNextCycleRetries()
    {
        var purger = new FlakyPurger();
        var services = new ServiceCollection().AddSingleton<IRefreshTokenPurger>(purger).BuildServiceProvider();
        var logger = new ListLogger<RefreshTokenPurgeService>();
        var service = NewService(services, new RefreshTokenPurgeOptions { StartupDelay = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(30), BatchSize = 10 }, logger, TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => purger.Calls >= 3);
        await service.StopAsync(CancellationToken.None);

        Assert.True(purger.Calls >= 3, "El servicio dejó de ejecutar ciclos tras el primer fallo.");
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokensPurged); // un fallo no es un evento de seguridad
        Assert.Contains("Falló un ciclo", error.Message);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Service_Disabled_DoesNothing()
    {
        await ResetAsync();
        await SeedAsync(NewRow(Cutoff.AddDays(-5)));
        using var provider = Provider();
        var service = NewService(provider, new RefreshTokenPurgeOptions { Enabled = false, StartupDelay = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(20) }, time: TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync()); // la fila de hace 12 días sigue ahí: nadie purgó
    }

    [Fact]
    public async Task Service_Enabled_PurgesAfterTheStartupDelay_ThenOnEveryInterval()
    {
        // Control del anterior: el mismo montaje con la purga encendida sí borra.
        await ResetAsync();
        await SeedAsync(NewRow(DateTime.UtcNow.AddDays(-20)));
        using var provider = Provider();
        var service = NewService(provider, new RefreshTokenPurgeOptions { StartupDelay = TimeSpan.FromMilliseconds(100), Interval = TimeSpan.FromMilliseconds(100) }, time: TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => CountAsync().GetAwaiter().GetResult() == 0);
        await SeedAsync(NewRow(DateTime.UtcNow.AddDays(-30))); // una más, que el SIGUIENTE ciclo debe encontrar
        await WaitUntilAsync(() => CountAsync().GetAwaiter().GetResult() == 0);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task Service_DoesNotStartPurgingBeforeTheStartupDelay_AndStopsCleanlyDuringIt()
    {
        await ResetAsync();
        await SeedAsync(NewRow(DateTime.UtcNow.AddDays(-20)));
        using var provider = Provider();
        var service = NewService(provider, new RefreshTokenPurgeOptions { StartupDelay = TimeSpan.FromHours(1) }, time: TimeProvider.System);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal(1, await CountAsync()); // aún esperando: no compite con el arranque (R4)

        var stopping = service.StopAsync(CancellationToken.None);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5)); // se detiene con la API sin esperar la hora

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(1, await CountAsync());
    }

    // ===== La configuración y el registro =====

    [Fact]
    public void Options_Defaults_AreTheApprovedOnes_AndAreValid()
    {
        var options = new RefreshTokenPurgeOptions();

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(6), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(2), options.StartupDelay);
        Assert.Equal(7, options.RetentionDays);
        Assert.Equal(1000, options.BatchSize);
        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData("Interval", "00:00:00")]
    [InlineData("Interval", "-00:00:05")]
    [InlineData("StartupDelay", "-00:00:01")]
    [InlineData("PauseBetweenBatches", "-00:00:01")]
    [InlineData("RetentionDays", "0")]
    [InlineData("RetentionDays", "-3")]
    [InlineData("RetentionDays", "3651")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "10001")]
    public void Options_Validate_RejectsEachInvalidValue(string name, string value)
    {
        var options = new RefreshTokenPurgeOptions();
        switch (name)
        {
            case "Interval": options.Interval = TimeSpan.Parse(value); break;
            case "StartupDelay": options.StartupDelay = TimeSpan.Parse(value); break;
            case "PauseBetweenBatches": options.PauseBetweenBatches = TimeSpan.Parse(value); break;
            case "RetentionDays": options.RetentionDays = int.Parse(value); break;
            case "BatchSize": options.BatchSize = int.Parse(value); break;
        }

        Assert.NotNull(options.Validate());
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void AddRefreshTokenPurge_WithAnInvalidSection_FailsWhenTheOptionsAreRead_NotLaterInSilence()
    {
        var services = new ServiceCollection();
        services.AddRefreshTokenPurge(Config(("RefreshTokenPurge:RetentionDays", "0")));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RefreshTokenPurgeOptions>>().Value);
    }

    [Fact]
    public void AddRefreshTokenPurge_BindsTheSection_AndRegistersTheHostedService()
    {
        var services = new ServiceCollection();
        services.AddRefreshTokenPurge(Config(("RefreshTokenPurge:RetentionDays", "14"), ("RefreshTokenPurge:Interval", "01:00:00"), ("RefreshTokenPurge:Enabled", "false")));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RefreshTokenPurgeOptions>>().Value;
        Assert.Equal(14, options.RetentionDays);
        Assert.Equal(TimeSpan.FromHours(1), options.Interval);
        Assert.False(options.Enabled);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RefreshTokenPurgeService));
    }

    [Fact]
    public void TheAdminConsole_NeverGetsTheBackgroundService()
    {
        // La consola de administración es interactiva y de un solo uso: no debe lanzar tareas periódicas.
        var services = new ServiceCollection();
        services.AddAdminInfrastructure(Config(
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Mfa:EncryptionKey", Convert.ToBase64String(new byte[32]))));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }
}

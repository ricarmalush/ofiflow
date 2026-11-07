using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Persistence.Configurations;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La tabla de tokens de restablecimiento de contraseña (spec 018, bloque 1) contra SQL Server real: el esquema, el índice único, el control de concurrencia
/// (que una base en memoria no aplica) y la purga por lotes, sola y dentro del ciclo del servicio. Cada caso de «se borra» va con su control de «no se borra».
/// </summary>
public class PasswordResetTokenStoreTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
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

    /// <summary>Todos los tests de la clase comparten el contenedor, y la purga es global: cada uno empieza con las dos tablas vacías.</summary>
    private async Task ResetAsync()
    {
        await using var db = Open();
        await db.PasswordResetTokens.ExecuteDeleteAsync();
        await db.RefreshTokens.ExecuteDeleteAsync();
    }

    // Un token que caduca en `expiresAt`: la caducidad es la creación más una hora.
    private static PasswordResetToken Row(DateTime expiresAt, bool used = false, Guid? userId = null)
    {
        var token = PasswordResetToken.Create(userId ?? Guid.NewGuid(), Guid.NewGuid().ToString("N"), expiresAt - PasswordResetToken.Lifetime);
        if (used)
        {
            token.Invalidate(expiresAt - TimeSpan.FromMinutes(30));
        }

        return token;
    }

    private async Task SeedAsync(params PasswordResetToken[] rows)
    {
        await using var db = Open();
        db.PasswordResetTokens.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync()
    {
        await using var db = Open();
        return await db.PasswordResetTokens.CountAsync();
    }

    private async Task<int> PurgeBatchAsync(int batchSize)
    {
        await using var db = Open();
        return await new PasswordResetService(db, TimeProvider.System).PurgeBatchAsync(Cutoff, batchSize, CancellationToken.None);
    }

    // ===== El esquema =====

    [Fact]
    public async Task ARowIsStoredAndReadBack_WithAllItsFields()
    {
        await ResetAsync();
        var token = PasswordResetToken.Create(Guid.NewGuid(), "hash-1234", Now);
        await SeedAsync(token);

        await using var db = Open();
        var read = await db.PasswordResetTokens.SingleAsync(t => t.Id == token.Id);

        Assert.Equal(token.UserId, read.UserId);
        Assert.Equal("hash-1234", read.TokenHash);
        Assert.Equal(Now, read.CreatedAt);
        Assert.Equal(Now.AddHours(1), read.ExpiresAt);
        Assert.Null(read.UsedAt);
    }

    [Fact]
    public async Task TheTable_HasNoColumnThatCouldHoldAnEmailOrTheTokenItself()
    {
        await using var db = Open();
        var entity = db.Model.FindEntityType(typeof(PasswordResetToken))!;

        var columns = entity.GetProperties().Select(p => p.Name).Order().ToList();

        // R17: solo el Id de la cuenta y el hash. Ni email, ni nombre, ni el valor del token.
        Assert.Equal(["CreatedAt", "ExpiresAt", "Id", "RowVersion", "TokenHash", "UsedAt", "UserId"], columns);
    }

    [Fact]
    public async Task TheHashColumn_HasTheLengthDomainDefines_AndIsRequired()
    {
        await using var db = Open();
        var property = db.Model.FindEntityType(typeof(PasswordResetToken))!.FindProperty(nameof(PasswordResetToken.TokenHash))!;

        Assert.Equal(PasswordResetToken.TokenHashMaxLength, property.GetMaxLength());
        Assert.False(property.IsNullable);
    }

    [Fact]
    public async Task TheIndexes_AreTheOnesTheQueriesNeed()
    {
        await using var db = Open();
        var entity = db.Model.FindEntityType(typeof(PasswordResetToken))!;

        var indexes = entity.GetIndexes().ToDictionary(i => string.Join("+", i.Properties.Select(p => p.Name)), i => i.IsUnique);

        Assert.True(indexes["TokenHash"]);                    // único: se busca por el hash
        Assert.False(indexes["UserId+CreatedAt"]);            // «cuántos en la última hora» y «anular los vigentes de la cuenta»
        Assert.False(indexes["ExpiresAt"]);                   // la purga
        Assert.Equal(3, indexes.Count);
    }

    [Fact]
    public async Task TheTable_HasNoForeignKey_SoNothingCascades()
    {
        await using var db = Open();

        Assert.Empty(db.Model.FindEntityType(typeof(PasswordResetToken))!.GetForeignKeys());
    }

    [Fact]
    public async Task TwoTokensWithTheSameHash_AreRefused()
    {
        await ResetAsync();
        await SeedAsync(PasswordResetToken.Create(Guid.NewGuid(), "mismo-hash", Now));

        await using var db = Open();
        db.PasswordResetTokens.Add(PasswordResetToken.Create(Guid.NewGuid(), "mismo-hash", Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoTokensOfTheSameAccount_AreAllowed()
    {
        // Control del test de arriba: lo único (el hash) es el hash, no la cuenta (el límite de 3 por hora lo aplica el caso de uso).
        await ResetAsync();
        var user = Guid.NewGuid();

        await SeedAsync(PasswordResetToken.Create(user, "hash-a", Now), PasswordResetToken.Create(user, "hash-b", Now));

        Assert.Equal(2, await CountAsync());
    }

    // ===== Concurrencia (R10) =====

    [Fact]
    public async Task TwoConsumptionsOfTheSameToken_OnlyTheFirstOneIsSaved()
    {
        await ResetAsync();
        var token = PasswordResetToken.Create(Guid.NewGuid(), "hash-carrera", Now);
        await SeedAsync(token);

        // Dos peticiones leen el token a la vez como utilizable...
        await using var first = Open();
        await using var second = Open();
        var a = await first.PasswordResetTokens.SingleAsync(t => t.Id == token.Id);
        var b = await second.PasswordResetTokens.SingleAsync(t => t.Id == token.Id);
        a.Consume(Now.AddMinutes(1));
        b.Consume(Now.AddMinutes(1));

        // ...y la base de datos solo deja confirmar a una: la versión de fila cambió bajo los pies de la segunda.
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TheRowVersion_IsTheShadowColumnTheConfigurationNames()
    {
        await using var db = Open();
        var property = db.Model.FindEntityType(typeof(PasswordResetToken))!.FindProperty(PasswordResetTokenConfiguration.RowVersion)!;

        Assert.True(property.IsConcurrencyToken);
        Assert.True(property.IsShadowProperty());
    }

    // ===== Un lote (PasswordResetService.PurgeBatchAsync) =====

    [Fact]
    public async Task PurgeBatch_DeletesTokensExpiredBeforeTheCutoff_UsedOrNot_OfAnyAccount()
    {
        await ResetAsync();
        await SeedAsync(
            Row(Cutoff.AddDays(-1)),                  // pedido y nunca usado
            Row(Cutoff.AddDays(-1), used: true),      // usado
            Row(Cutoff.AddMinutes(-1)));              // otra cuenta, por poco

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(3, deleted);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_KeepsEverythingThatIsNotExpiredBeforeTheCutoff()
    {
        await ResetAsync();
        await SeedAsync(
            Row(Now.AddMinutes(30)),                          // vigente
            Row(Now.AddMinutes(30), used: true),              // usado pero aún sin caducar
            Row(Now.AddMinutes(-1)),                          // caducó hace un minuto
            Row(Cutoff.AddMinutes(1)),                        // caducó dentro del margen
            Row(Cutoff.AddMinutes(1), used: true));           // usado y dentro del margen

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(0, deleted);
        Assert.Equal(5, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_TheExactCutoffInstant_IsKept()
    {
        // Caducó EXACTAMENTE en el corte: no es «anterior al corte», se conserva (así el margen nunca es más corto que el configurado).
        await ResetAsync();
        await SeedAsync(Row(Cutoff));

        Assert.Equal(0, await PurgeBatchAsync(100));
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_RespectsTheBatchSize_AndDeletesTheOldestFirst()
    {
        await ResetAsync();
        var oldest = Row(Cutoff.AddDays(-10));
        var middle = Row(Cutoff.AddDays(-5));
        var newest = Row(Cutoff.AddDays(-1));
        await SeedAsync(newest, oldest, middle);

        var deleted = await PurgeBatchAsync(2);

        Assert.Equal(2, deleted);
        await using var db = Open();
        Assert.Equal([newest.Id], await db.PasswordResetTokens.Select(t => t.Id).ToListAsync());
    }

    [Fact]
    public async Task PurgeBatch_DoesNotTouchTheRefreshTokens()
    {
        // Dos tablas, dos purgas: borrar una no puede llevarse la otra.
        await ResetAsync();
        await using (var db = Open())
        {
            db.RefreshTokens.Add(new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), Cutoff.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        await SeedAsync(Row(Cutoff.AddDays(-1)));

        await PurgeBatchAsync(100);

        await using var check = Open();
        Assert.Equal(1, await check.RefreshTokens.CountAsync());
        Assert.Equal(0, await check.PasswordResetTokens.CountAsync());
    }

    // ===== Un ciclo entero (RefreshTokenPurgeService.RunCycleAsync, R18) =====

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(_fixture.ConnectionString));
        services.AddSingleton<IOptions<JwtOptions>>(Options.Create(Jwt));
        services.AddSingleton<ILogger<TokenService>>(NullLogger<TokenService>.Instance);
        services.AddSingleton<IClientInfo>(new FakeClientInfo());
        services.AddScoped<TokenService>();
        services.AddScoped<IRefreshTokenPurger>(provider => provider.GetRequiredService<TokenService>());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PasswordResetService>();
        services.AddScoped<IPasswordResetTokenPurger>(provider => provider.GetRequiredService<PasswordResetService>());
        // El ciclo purga también los registros pendientes (spec 021 R18).
        services.AddScoped<PendingRegistrationService>();
        services.AddScoped<IPendingRegistrationPurger>(provider => provider.GetRequiredService<PendingRegistrationService>());

        return services.BuildServiceProvider();
    }

    private static RefreshTokenPurgeService NewService(IServiceProvider provider, ListLogger<RefreshTokenPurgeService> logger, int batchSize = 3) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new RefreshTokenPurgeOptions { BatchSize = batchSize, PauseBetweenBatches = TimeSpan.Zero }),
            new FakeTimeProvider(Now), logger);

    [Fact]
    public async Task RunCycle_PurgesBothTables_InSeveralBatches_AndLeavesTheirOwnEvents()
    {
        await ResetAsync();
        await SeedAsync([.. Enumerable.Range(0, 8).Select(i => Row(Cutoff.AddDays(-1 - i), used: i % 2 == 0))]);
        await SeedAsync(Row(Now.AddMinutes(30)), Row(Cutoff.AddDays(1)));  // las que se quedan
        await using (var db = Open())
        {
            db.RefreshTokens.AddRange(Enumerable.Range(0, 4).Select(i =>
                new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), Cutoff.AddDays(-1 - i))));
            await db.SaveChangesAsync();
        }

        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        var total = await NewService(provider, logger).RunCycleAsync(CancellationToken.None);

        Assert.Equal(12, total);                  // 4 de refresh tokens + 8 de restablecimiento
        Assert.Equal(2, await CountAsync());
        var resetEvent = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetTokensPurged);
        Assert.Contains("8 filas", resetEvent.Message);
        var refreshEvent = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokensPurged);
        Assert.Contains("4 filas", refreshEvent.Message);
    }

    [Fact]
    public async Task RunCycle_WithNothingToDeleteInResetTokens_LeavesNoResetEvent()
    {
        await ResetAsync();
        await SeedAsync(Row(Now.AddMinutes(30)));
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        await NewService(provider, logger).RunCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetTokensPurged);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task RunCycle_TheResetEvent_CarriesNoHashNorAccount()
    {
        await ResetAsync();
        var user = Guid.NewGuid();
        var token = Row(Cutoff.AddDays(-1), userId: user);
        await SeedAsync(token);
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        await NewService(provider, logger).RunCycleAsync(CancellationToken.None);

        var everything = string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain(token.TokenHash, everything);
        Assert.DoesNotContain(user.ToString(), everything);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Persistence.Configurations;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La tabla de registros pendientes (spec 021, bloque 1) contra SQL Server real: el esquema, los índices (sobre todo el único FILTRADO que impide dos vigentes por email), la versión
/// de fila (que una base en memoria no aplica) y la purga por lotes, sola y dentro del ciclo del servicio. Cada caso de «se borra» va con su control de «no se borra».
/// </summary>
public class PendingRegistrationStoreTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
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

    /// <summary>La purga es global y los tests de la clase comparten el contenedor: cada uno empieza con las tres tablas vacías.</summary>
    private async Task ResetAsync()
    {
        await using var db = Open();
        await db.PendingRegistrations.ExecuteDeleteAsync();
        await db.PasswordResetTokens.ExecuteDeleteAsync();
        await db.RefreshTokens.ExecuteDeleteAsync();
    }

    // Un pendiente que caduca en `expiresAt`: la caducidad es la creación más un día. Cada fila, con su email y su hash propios.
    private static PendingRegistration Row(DateTime expiresAt, bool used = false, string? email = null)
    {
        var pending = PendingRegistration.Create(
            email ?? $"p-{Guid.NewGuid():N}@example.com", "Empresa", "Ana", "2026-11", Guid.NewGuid().ToString("N"), expiresAt - PendingRegistration.Lifetime);
        if (used)
        {
            pending.Invalidate(expiresAt - TimeSpan.FromHours(1));
        }

        return pending;
    }

    private async Task SeedAsync(params PendingRegistration[] rows)
    {
        await using var db = Open();
        db.PendingRegistrations.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync()
    {
        await using var db = Open();
        return await db.PendingRegistrations.CountAsync();
    }

    private async Task<int> PurgeBatchAsync(int batchSize)
    {
        await using var db = Open();
        return await new PendingRegistrationService(db, TimeProvider.System).PurgeBatchAsync(Cutoff, batchSize, CancellationToken.None);
    }

    // ===== El esquema =====

    [Fact]
    public async Task ARowIsStoredAndReadBack_WithAllItsFields()
    {
        await ResetAsync();
        var pending = PendingRegistration.Create("Ana.Perez@Example.com", "Fontanería Demo SL", "Ana Pérez", "2026-11", "hash-1234", Now);
        await SeedAsync(pending);

        await using var db = Open();
        var read = await db.PendingRegistrations.SingleAsync(r => r.Id == pending.Id);

        Assert.Equal("Ana.Perez@Example.com", read.Email);
        Assert.Equal("ANA.PEREZ@EXAMPLE.COM", read.NormalizedEmail);
        Assert.Equal("Fontanería Demo SL", read.CompanyName);
        Assert.Equal("Ana Pérez", read.UserName);
        Assert.Equal("2026-11", read.TermsVersion);
        Assert.Equal(Now, read.TermsAcceptedAt);
        Assert.Equal("hash-1234", read.TokenHash);
        Assert.Equal(Now, read.CreatedAt);
        Assert.Equal(Now.AddHours(24), read.ExpiresAt);
        Assert.Null(read.UsedAt);
    }

    [Fact]
    public async Task TheTable_HasExactlyTheColumnsTheSpecNames_AndNoPasswordNorTenant()
    {
        await using var db = Open();
        var entity = db.Model.FindEntityType(typeof(PendingRegistration))!;

        var columns = entity.GetProperties().Select(p => p.Name).Order().ToList();

        // R4: lo escrito al pedir el registro y el hash del token. NADA de contraseña (se elige al completar, R10) ni de empresa o usuario creados: todavía no existen.
        Assert.Equal(
            ["CompanyName", "CreatedAt", "Email", "ExpiresAt", "Id", "NormalizedEmail", "RowVersion", "TermsAcceptedAt", "TermsVersion", "TokenHash", "UsedAt", "UserName"],
            columns);
    }

    [Fact]
    public async Task TheColumns_HaveTheLengthsDomainDefines_AndAreRequired()
    {
        await using var db = Open();
        var entity = db.Model.FindEntityType(typeof(PendingRegistration))!;

        Assert.Equal(PendingRegistration.TokenHashMaxLength, entity.FindProperty(nameof(PendingRegistration.TokenHash))!.GetMaxLength());
        Assert.Equal(User.TermsVersionMaxLength, entity.FindProperty(nameof(PendingRegistration.TermsVersion))!.GetMaxLength());
        Assert.Equal(User.NameMaxLength, entity.FindProperty(nameof(PendingRegistration.UserName))!.GetMaxLength());
        foreach (var name in new[] { nameof(PendingRegistration.Email), nameof(PendingRegistration.NormalizedEmail), nameof(PendingRegistration.CompanyName), nameof(PendingRegistration.UserName),
                                     nameof(PendingRegistration.TermsVersion), nameof(PendingRegistration.TokenHash) })
        {
            Assert.False(entity.FindProperty(name)!.IsNullable, name);
        }
    }

    [Fact]
    public async Task TheIndexes_AreTheOnesTheQueriesNeed()
    {
        await using var db = Open();
        var entity = db.Model.FindEntityType(typeof(PendingRegistration))!;

        var indexes = entity.GetIndexes().ToList();

        var byName = indexes.ToDictionary(i => i.GetDatabaseName()!, i => i);
        Assert.Equal(4, indexes.Count);
        Assert.True(byName["IX_PendingRegistrations_TokenHash"].IsUnique);                       // se busca por el hash
        Assert.True(byName["IX_PendingRegistrations_NormalizedEmail_Active"].IsUnique);          // un solo vigente por email
        Assert.Equal("[UsedAt] IS NULL", byName["IX_PendingRegistrations_NormalizedEmail_Active"].GetFilter());
        Assert.False(byName["IX_PendingRegistrations_NormalizedEmail_CreatedAt"].IsUnique);      // «cuántos en la última hora»
        Assert.False(byName["IX_PendingRegistrations_ExpiresAt"].IsUnique);                      // la purga
    }

    [Fact]
    public async Task TheTable_HasNoForeignKey_SoNothingCascades()
    {
        await using var db = Open();

        Assert.Empty(db.Model.FindEntityType(typeof(PendingRegistration))!.GetForeignKeys());
    }

    [Fact]
    public async Task TheTable_IsNotFilteredByTenant_ThereIsNoTenantYet()
    {
        await using var db = Open();
        var entity = db.Model.FindEntityType(typeof(PendingRegistration))!;

        Assert.Null(entity.FindProperty("TenantId"));
        Assert.Empty(entity.GetDeclaredQueryFilters());
    }

    // ===== Los dos índices únicos, contra la base de datos =====

    [Fact]
    public async Task TwoRowsWithTheSameTokenHash_AreRefused()
    {
        await ResetAsync();
        await SeedAsync(PendingRegistration.Create("uno@example.com", "E", "A", "2026-11", "mismo-hash", Now));

        await using var db = Open();
        db.PendingRegistrations.Add(PendingRegistration.Create("dos@example.com", "E", "A", "2026-11", "mismo-hash", Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoLiveRowsForTheSameEmail_AreRefused_EvenIfTheyAreWrittenWithAnotherCase()
    {
        await ResetAsync();
        await SeedAsync(PendingRegistration.Create("misma@example.com", "E", "A", "2026-11", "hash-a", Now));

        await using var db = Open();
        db.PendingRegistrations.Add(PendingRegistration.Create("MISMA@Example.com", "E", "A", "2026-11", "hash-b", Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ALiveRowAndAnInvalidatedOneForTheSameEmail_AreAllowed_TheFilterOfTheIndex()
    {
        // Control del test de arriba: el índice solo mira las filas sin canjear ni anular. Una anulada, o una canjeada, no impide que haya una vigente.
        await ResetAsync();
        var old = PendingRegistration.Create("misma@example.com", "E", "A", "2026-11", "hash-a", Now);
        old.Invalidate(Now.AddMinutes(1));
        var used = PendingRegistration.Create("misma@example.com", "E", "A", "2026-11", "hash-b", Now);
        used.Consume(Now.AddMinutes(2));
        var live = PendingRegistration.Create("misma@example.com", "E", "A", "2026-11", "hash-c", Now);

        await SeedAsync(old, used, live);

        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task TwoLiveRowsForDifferentEmails_AreAllowed()
    {
        await ResetAsync();

        await SeedAsync(PendingRegistration.Create("a@example.com", "E", "A", "2026-11", "hash-a", Now), PendingRegistration.Create("b@example.com", "E", "A", "2026-11", "hash-b", Now));

        Assert.Equal(2, await CountAsync());
    }

    // ===== Concurrencia (R13) =====

    [Fact]
    public async Task TwoConsumptionsOfTheSameRow_OnlyTheFirstOneIsSaved()
    {
        await ResetAsync();
        var pending = PendingRegistration.Create("carrera@example.com", "E", "A", "2026-11", "hash-carrera", Now);
        await SeedAsync(pending);

        await using var first = Open();
        await using var second = Open();
        var a = await first.PendingRegistrations.SingleAsync(r => r.Id == pending.Id);
        var b = await second.PendingRegistrations.SingleAsync(r => r.Id == pending.Id);
        a.Consume(Now.AddMinutes(1));
        b.Consume(Now.AddMinutes(1));

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TheRowVersion_IsTheShadowColumnTheConfigurationNames()
    {
        await using var db = Open();
        var property = db.Model.FindEntityType(typeof(PendingRegistration))!.FindProperty(PendingRegistrationConfiguration.RowVersion)!;

        Assert.True(property.IsConcurrencyToken);
        Assert.True(property.IsShadowProperty());
    }

    // ===== Un lote (PendingRegistrationService.PurgeBatchAsync) =====

    [Fact]
    public async Task PurgeBatch_DeletesRowsExpiredBeforeTheCutoff_UsedOrNot()
    {
        await ResetAsync();
        await SeedAsync(
            Row(Cutoff.AddDays(-1)),                  // pedido y nunca canjeado: datos personales de alguien que quizá nunca tuvo cuenta
            Row(Cutoff.AddDays(-1), used: true),      // canjeado o anulado
            Row(Cutoff.AddMinutes(-1)));              // por poco

        var deleted = await PurgeBatchAsync(100);

        Assert.Equal(3, deleted);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task PurgeBatch_KeepsEverythingThatIsNotExpiredBeforeTheCutoff()
    {
        await ResetAsync();
        await SeedAsync(
            Row(Now.AddHours(5)),                             // vigente
            Row(Now.AddHours(5), used: true),                 // canjeado pero aún sin caducar
            Row(Now.AddMinutes(-1)),                          // caducó hace un minuto
            Row(Cutoff.AddMinutes(1)),                        // caducó dentro del margen
            Row(Cutoff.AddMinutes(1), used: true));           // canjeado y dentro del margen

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
        Assert.Equal([newest.Id], await db.PendingRegistrations.Select(r => r.Id).ToListAsync());
    }

    [Fact]
    public async Task PurgeBatch_DoesNotTouchTheOtherTwoTables()
    {
        // Tres tablas, tres purgas: borrar una no puede llevarse las otras.
        await ResetAsync();
        await using (var db = Open())
        {
            db.RefreshTokens.Add(new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), Cutoff.AddDays(-1)));
            db.PasswordResetTokens.Add(PasswordResetToken.Create(Guid.NewGuid(), Guid.NewGuid().ToString("N"), Cutoff.AddDays(-2)));
            await db.SaveChangesAsync();
        }

        await SeedAsync(Row(Cutoff.AddDays(-1)));

        await PurgeBatchAsync(100);

        await using var check = Open();
        Assert.Equal(1, await check.RefreshTokens.CountAsync());
        Assert.Equal(1, await check.PasswordResetTokens.CountAsync());
        Assert.Equal(0, await check.PendingRegistrations.CountAsync());
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
        services.AddScoped<PendingRegistrationService>();
        services.AddScoped<IPendingRegistrationPurger>(provider => provider.GetRequiredService<PendingRegistrationService>());

        return services.BuildServiceProvider();
    }

    private static RefreshTokenPurgeService NewService(IServiceProvider provider, ListLogger<RefreshTokenPurgeService> logger, int batchSize = 3) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new RefreshTokenPurgeOptions { BatchSize = batchSize, PauseBetweenBatches = TimeSpan.Zero }),
            new FakeTimeProvider(Now), logger);

    [Fact]
    public async Task RunCycle_PurgesThePendingRegistrations_InSeveralBatches_AndLeavesItsOwnEvent()
    {
        await ResetAsync();
        await SeedAsync([.. Enumerable.Range(0, 8).Select(i => Row(Cutoff.AddDays(-1 - i), used: i % 2 == 0))]);
        await SeedAsync(Row(Now.AddHours(5)), Row(Cutoff.AddDays(1))); // las que se quedan
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        var total = await NewService(provider, logger).RunCycleAsync(CancellationToken.None);

        Assert.Equal(8, total);
        Assert.Equal(2, await CountAsync());
        var pendingEvent = Assert.Single(logger.Entries, e => e.EventId.Id == SecurityEventIds.PendingRegistrationsPurged);
        Assert.Contains("8 filas", pendingEvent.Message);
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetTokensPurged);
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.RefreshTokensPurged);
    }

    [Fact]
    public async Task RunCycle_WithNothingToDelete_LeavesNoEvent()
    {
        await ResetAsync();
        await SeedAsync(Row(Now.AddHours(5)));
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        await NewService(provider, logger).RunCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == SecurityEventIds.PendingRegistrationsPurged);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task RunCycle_TheEvent_CarriesNoEmailNorNameNorHash()
    {
        await ResetAsync();
        var row = Row(Cutoff.AddDays(-1), email: "persona.privada@example.com");
        await SeedAsync(row);
        var logger = new ListLogger<RefreshTokenPurgeService>();
        using var provider = Provider();

        await NewService(provider, logger).RunCycleAsync(CancellationToken.None);

        var everything = string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("persona.privada", everything);
        Assert.DoesNotContain(row.TokenHash, everything);
        Assert.DoesNotContain(row.UserName, everything);
        Assert.DoesNotContain(row.CompanyName, everything);
    }
}

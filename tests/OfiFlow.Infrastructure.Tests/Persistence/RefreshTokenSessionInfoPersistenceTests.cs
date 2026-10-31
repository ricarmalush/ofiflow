using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Los datos de la sesión (spec 016, bloque 2) contra SQL Server real: que viajan a la base de datos y vuelven, y que el esquema que deja la migración
/// es el que debe. La trampa de esta migración (la misma de la spec 015) es que EF crea las fechas nuevas con un valor por defecto de 0001-01-01: si
/// alguna fila lo conservara, una sesión «habría empezado en el año 1».
/// </summary>
public class RefreshTokenSessionInfoPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private static RefreshToken Row(string? userAgent, string? ip, DateTime? createdAt = null, DateTime? startedAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddDays(7), createdAt, startedAt, userAgent, ip);

    [Fact]
    public async Task TheSessionData_RoundTripsThroughTheDatabase()
    {
        var created = new DateTime(2026, 10, 30, 9, 15, 0, DateTimeKind.Utc);
        var started = created.AddHours(-3);
        var row = Row("Mozilla/5.0 (Windows NT 10.0) Chrome/120", "203.0.113.0", created, started);
        await using (var db = Open())
        {
            db.RefreshTokens.Add(row);
            await db.SaveChangesAsync();
        }

        await using var read = Open();
        var loaded = await read.RefreshTokens.AsNoTracking().SingleAsync(rt => rt.Id == row.Id);

        Assert.Equal("Mozilla/5.0 (Windows NT 10.0) Chrome/120", loaded.UserAgent);
        Assert.Equal("203.0.113.0", loaded.IpAddress);
        Assert.Equal(created, loaded.CreatedAt);
        Assert.Equal(started, loaded.SessionStartedAt);
    }

    [Fact]
    public async Task ARowWithNoDeviceData_IsStoredWithNulls_ButNeverWithAYearOneDate()
    {
        var row = Row(userAgent: null, ip: null);
        await using (var db = Open())
        {
            db.RefreshTokens.Add(row);
            await db.SaveChangesAsync();
        }

        await using var read = Open();
        var loaded = await read.RefreshTokens.AsNoTracking().SingleAsync(rt => rt.Id == row.Id);

        Assert.Null(loaded.UserAgent);
        Assert.Null(loaded.IpAddress);
        Assert.True(loaded.CreatedAt.Year >= 2026, $"CreatedAt llegó como {loaded.CreatedAt:O}");
        Assert.True(loaded.SessionStartedAt.Year >= 2026, $"SessionStartedAt llegó como {loaded.SessionStartedAt:O}");
    }

    [Fact]
    public async Task TheLongestAllowedValues_FitTheColumns()
    {
        var row = Row(new string('a', ClientInfo.MaxUserAgentLength), "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff-pad-to-45-chars"[..ClientInfo.MaxIpLength]);
        await using var db = Open();
        db.RefreshTokens.Add(row);

        await db.SaveChangesAsync();

        Assert.Equal(ClientInfo.MaxUserAgentLength, (await db.RefreshTokens.AsNoTracking().SingleAsync(rt => rt.Id == row.Id)).UserAgent!.Length);
    }

    [Fact]
    public async Task AUserAgentOverTheLimit_IsRejectedByTheDatabase_SoTheSanitizerIsNotOptional()
    {
        // Defensa en profundidad: la base de datos se niega a guardar más de lo que cabe, en vez de recortar en silencio. Lo que evita que esto ocurra
        // en producción es que ClientInfo recorta ANTES; aquí se comprueba que la columna de verdad tiene ese tope.
        await using var db = Open();
        db.RefreshTokens.Add(Row(new string('a', ClientInfo.MaxUserAgentLength + 1), ip: null));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TheSchemaTheMigrationLeaves_IsTheOneThatIsExpected()
    {
        await using var db = Open();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.COLUMN_NAME, c.IS_NULLABLE, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.COLUMN_DEFAULT " +
            "FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_NAME = 'RefreshTokens' AND c.COLUMN_NAME IN ('CreatedAt','SessionStartedAt','UserAgent','IpAddress')";

        var columns = new Dictionary<string, (string Nullable, string Type, int? Length, string? Default)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        Assert.Equal(4, columns.Count);
        // Las dos fechas: NOT NULL y SIN valor por defecto (si tuvieran uno, un olvido del código crearía una fila «del año 1» sin quejarse).
        Assert.Equal(("NO", "datetime2", (int?)null, (string?)null), columns["CreatedAt"]);
        Assert.Equal(("NO", "datetime2", (int?)null, (string?)null), columns["SessionStartedAt"]);
        // El navegador y la red: anulables (una sesión anterior no los tiene) y con el tope de los saneadores.
        Assert.Equal(("YES", "nvarchar", (int?)ClientInfo.MaxUserAgentLength, (string?)null), columns["UserAgent"]);
        Assert.Equal(("YES", "nvarchar", (int?)ClientInfo.MaxIpLength, (string?)null), columns["IpAddress"]);
    }
}

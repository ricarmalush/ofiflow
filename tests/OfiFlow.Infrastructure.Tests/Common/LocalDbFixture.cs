using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Tests.Common;

/// <summary>
/// Crea una base de datos con nombre único en la instancia local de LocalDB, aplica las
/// migraciones reales de EF Core, y la elimina al terminar — el mismo aislamiento por
/// ejecución que daría un contenedor Docker, sin necesitarlo (ADR-008).
/// </summary>
public sealed class LocalDbFixture : IAsyncLifetime
{
    private readonly string _connectionString =
        $"Server=(localdb)\\mssqllocaldb;Database=OfiFlowTests_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True;";

    public async Task InitializeAsync()
    {
        await using var context = CreateDbContext(Guid.Empty);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var context = CreateDbContext(Guid.Empty);
        await context.Database.EnsureDeletedAsync();
    }

    public ApplicationDbContext CreateDbContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

        return new ApplicationDbContext(options, new FixedTenantContext(tenantId));
    }
}

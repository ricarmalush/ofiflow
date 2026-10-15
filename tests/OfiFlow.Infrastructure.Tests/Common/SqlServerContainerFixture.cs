using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace OfiFlow.Infrastructure.Tests.Common;

/// <summary>
/// Levanta una SQL Server real en un contenedor Docker aislado por instancia de fixture,
/// aplica las migraciones reales de EF Core, y lo destruye al terminar (ADR-008) — el mismo
/// comportamiento en la máquina de desarrollo y en cualquier CI con Docker.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    // Imagen fijada explícitamente: los tests usan siempre la misma versión de SQL Server, en
    // local y en CI, en vez de la que traiga por defecto cada versión de Testcontainers.
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private readonly MsSqlContainer _container = new MsSqlBuilder(SqlServerImage).Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateDbContext(Guid.Empty);
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Igual que <see cref="CreateDbContext(Guid)"/>, pero permitiendo configurar el contexto (por ejemplo, añadir un interceptor que cuente las consultas).</summary>
    public ApplicationDbContext CreateDbContext(Guid tenantId, Action<DbContextOptionsBuilder<ApplicationDbContext>> configure)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(_container.GetConnectionString());
        configure(builder);

        return new ApplicationDbContext(builder.Options, new FixedTenantContext(tenantId));
    }

    public ApplicationDbContext CreateDbContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(_container.GetConnectionString())
            .Options;

        return new ApplicationDbContext(options, new FixedTenantContext(tenantId));
    }
}

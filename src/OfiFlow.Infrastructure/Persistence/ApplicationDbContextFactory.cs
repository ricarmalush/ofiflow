using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` commands at design time (migrations, database update), so they
/// work before Api wires up the real DI container. Never used at runtime.
/// Lee la cadena de conexión de la variable de entorno ConnectionStrings__Default si existe
/// (así es como dast.yml le pasa la SQL Server del contenedor de servicio en CI, donde
/// LocalDB no existe — ver ADR-008) y si no, usa la LocalDB local de cada desarrollador.
/// </summary>
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string LocalDbConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=OfiFlow;Trusted_Connection=True;TrustServerCertificate=True;";

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Default) ?? LocalDbConnectionString;

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new ApplicationDbContext(optionsBuilder.Options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
    }
}

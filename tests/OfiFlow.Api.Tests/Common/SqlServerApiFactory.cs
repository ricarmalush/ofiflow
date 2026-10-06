using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// La API real en memoria, conectada a un SQL Server real en Docker con las migraciones reales
/// (ADR-008, spec 007). Para los tests que necesitan recorrer el camino completo: token JWT →
/// <c>TenantContext</c> → filtro de tenant → base de datos. El resto de tests de la API usan
/// <see cref="OfiFlowApiFactory"/>, que no necesita Docker.
/// </summary>
public sealed class SqlServerApiFactory : WebApplicationFactory<Program>
{
    // Misma imagen que OfiFlow.Infrastructure.Tests (SqlServerContainerFixture): si se cambia, cambiar las dos.
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private readonly MsSqlContainer _container = new MsSqlBuilder(SqlServerImage).Build();

    /// <summary>Arranca el contenedor y aplica las migraciones. Hay que llamarlo antes de usar la factoría.</summary>
    public async Task StartAsync()
    {
        await _container.StartAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Garantía contra un fallo silencioso: appsettings.Development.json apunta a LocalDB. Si la
        // configuración del contenedor no se impusiera, los tests irían contra la base de datos del
        // desarrollador sin que nada lo avisara.
        // Se compara el servidor y puerto ya interpretados, no el texto: EF Core normaliza la cadena
        // (Data Source en vez de Server, y añade Application Name).
        var actualServer = new SqlConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var expectedServer = new SqlConnectionStringBuilder(_container.GetConnectionString()).DataSource;

        if (actualServer != expectedServer)
        {
            throw new InvalidOperationException("La API no está usando la base de datos del contenedor de pruebas.");
        }

        await db.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Un entorno propio, sin appsettings.Development.json: no hay cadena de conexión a LocalDB
        // que pueda colarse, y se parece más a producción (HSTS activo, sin OpenAPI).
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

        // Secreto efímero por ejecución: los tests no dependen de los user-secrets (ADR-009 R8).
        builder.UseSetting("Jwt:Secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Jwt:Issuer", "OfiFlow.Api.Tests");
        builder.UseSetting("Jwt:Audience", "OfiFlow.Api.Tests.Client");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _container.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

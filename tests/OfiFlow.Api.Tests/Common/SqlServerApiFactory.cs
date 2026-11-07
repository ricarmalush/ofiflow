using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
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

    /// <summary>
    /// Secreto efímero por ejecución: los tests no dependen de los user-secrets (ADR-009 R8). Se expone para
    /// que un test pueda fabricar tokens con una forma que el login nunca emite (sin rol, con un rol inválido).
    /// </summary>
    public string JwtSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>La clave de las sesiones de plataforma (spec 011 R7): distinta de la de empresa, para poder firmar tokens de los dos mundos en los tests.</summary>
    public string PlatformSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string JwtIssuer => "OfiFlow.Api.Tests";

    /// <summary>
    /// La versión vigente de las condiciones de uso (`Legal:TermsVersion`, spec 021). Aceptar una invitación y pedir un registro exigen enviarla (D8, R3): con ella configurada de serie, los
    /// tests que crean cuentas por invitación solo tienen que mandarla, y el caso «condiciones sin configurar» se prueba vaciándola a propósito.
    /// </summary>
    public const string TermsVersion = "2026-11";

    private AccountRegistrar? _registrar;

    /// <summary>
    /// ¿Se exige de verdad el segundo factor a Owner y Admin (spec 022)? Por defecto **no**: así los cientos de tests que usan un Owner o un Admin sin tratar de esto siguen funcionando sin activar el factor
    /// en cada fixture. Las clases que prueban la puerta lo ponen a verdadero **antes de arrancar**; entonces rige la regla real de producción.
    /// </summary>
    public bool EnforceSecondFactor { get; set; }

    /// <summary>
    /// Cómo se crea una empresa en los tests (spec 021, D11): pidiendo el registro, leyendo el enlace del correo y completándolo, como una persona. El registro inmediato ya no existe. Se crea
    /// la primera vez que se usa, porque levanta una segunda API sobre la misma base de datos.
    /// </summary>
    public AccountRegistrar Registrar => _registrar ??= new AccountRegistrar(this);

    /// <summary>Crea una empresa con su Owner por el flujo real y devuelve el Id de la empresa.</summary>
    public Task<Guid> RegisterCompanyAsync(string companyName, string userName, string email, string password) =>
        Registrar.RegisterAsync(companyName, userName, email, password);

    public string JwtAudience => "OfiFlow.Api.Tests.Client";

    /// <summary>Todo lo que la API ha registrado en el log, para comprobar eventos de seguridad (ADR-009 R5).</summary>
    public CapturingLoggerProvider Logs { get; } = new();

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

        builder.UseSetting("Jwt:Secret", JwtSecret);
        builder.UseSetting("Jwt:PlatformSecret", PlatformSecret);
        builder.UseSetting("Mfa:EncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Jwt:Issuer", JwtIssuer);

        // La purga de refresh tokens (spec 016) es un servicio en segundo plano: en los tests de la API no debe ponerse a borrar a mitad de una prueba.
        // Se prueba aparte, contra SQL Server real, llamando al ciclo directamente.
        builder.UseSetting("RefreshTokenPurge:Enabled", "false");
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("Legal:TermsVersion", TermsVersion);

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        // La regla de a quién se obliga el segundo factor: la real solo cuando el test la pide (ver EnforceSecondFactor).
        if (!EnforceSecondFactor)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecondFactorRequirement>();
                services.AddSingleton<ISecondFactorRequirement>(NoSecondFactorRequirement.Instance);
            });
        }

        // Solo en tests: cada petición puede declarar su IP (ver TestClientIpStartupFilter).
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestClientIpStartupFilter>());
    }

    public override async ValueTask DisposeAsync()
    {
        _registrar?.Dispose();
        await base.DisposeAsync();
        await _container.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

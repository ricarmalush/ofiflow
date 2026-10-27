using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Guardarraíles del alta y la baja de un operador de la plataforma (spec 011 R2 y R4, ADR-016): se hacen con un programa del
/// servidor, no por HTTP. Que ninguna ruta de la API llegue hasta ahí es justo lo que, si se rompe, no falla a la vista de
/// nadie: basta con registrar el servicio «por comodidad» o mapear un endpoint. Se escanea el código (ADR-009) y se comprueba
/// con un test de comportamiento que la herramienta no necesita la clave que firma las sesiones.
/// </summary>
public class PlatformOperatorToolTests
{
    private const string Administration = "PlatformOperatorAdministration";

    private const string AdministrationFile = "src/OfiFlow.Application/Platform/PlatformOperatorAdministration.cs";

    private const string AdminProgram = "src/OfiFlow.Admin/Program.cs";

    [Fact]
    public void TheAdministrationService_IsOnlyUsedByItsOwnFile_AndTheAdminProgram()
    {
        // Lista cerrada: su definición y el programa del servidor que la registra. Ni la API, ni Application (AddApplication
        // no la registra), ni Infrastructure. Si otro fichero la nombra, alguien puede estar a punto de exponerla.
        var users = SourceCode.FilesContaining(Administration).Order().ToList();

        Assert.Equal([AdminProgram, AdministrationFile], users);
    }

    [Fact]
    public void TheServerOnlyOperations_AreNeverCalledFromAnywhereElse()
    {
        // EnrollConfirmedAsync crea un segundo factor ya confirmado SIN comprobar ningún código (lo comprueba quien llama), y
        // FindUserIdAsync dice si un email existe. Las dos son para herramientas del servidor: solo las llama el servicio.
        Assert.Equal([AdministrationFile], SourceCode.FilesContaining(".EnrollConfirmedAsync(")); // control positivo: sí se llama, desde ahí
        Assert.Equal([AdministrationFile], SourceCode.FilesContaining(".FindUserIdAsync("));
    }

    [Fact]
    public void TheApi_DoesNotReferenceTheAdminProgram_NorItsRegistration()
    {
        // La API no puede ni compilar contra el programa del servidor...
        Assert.DoesNotContain("OfiFlow.Admin", SourceCode.ReadText("src/OfiFlow.Api/OfiFlow.Api.csproj"));
        Assert.Empty(SourceCode.FilesContaining("OfiFlow.Admin", "OfiFlow.Api"));

        // ...ni registrar lo que es suyo: AddAdminInfrastructure es para ese programa.
        Assert.Empty(SourceCode.FilesContaining("AddAdminInfrastructure", "OfiFlow.Api"));
        Assert.Equal([AdminProgram], SourceCode.FilesContaining("AddAdminInfrastructure(", "OfiFlow.Admin")); // control positivo
    }

    [Fact]
    public void ThePlatformOperatorOperations_AreNotMediatRRequests()
    {
        // Una petición de MediatR sería alcanzable desde cualquier endpoint (o desde el día de mañana, desde la IA) con solo
        // enviarla. El alta y la baja no lo son: viven en un servicio normal que solo conoce el programa del servidor.
        var service = SourceCode.ReadText(AdministrationFile);

        Assert.DoesNotContain("IRequest", service);
        Assert.DoesNotContain("IRequestHandler", service);
        Assert.Empty(SourceCode.Files().Where(file =>
            file.Content.Contains("IRequest<", StringComparison.Ordinal) &&
            (file.Content.Contains("CreatePlatformOperator", StringComparison.Ordinal) || file.Content.Contains("DisablePlatformOperator", StringComparison.Ordinal))));
    }

    [Fact]
    public void TheAdminProgram_NeverTakesThePasswordFromTheCommandLine()
    {
        // Un argumento queda en el historial de la shell y en la lista de procesos del servidor. La contraseña se pide por
        // la terminal, sin eco, y el programa no acepta configuración por la línea de comandos.
        var program = SourceCode.ReadText(AdminProgram);

        Assert.Contains("Prompt.ReadSecret(", program); // control positivo
        Assert.DoesNotContain("--password", program, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Args = args", program);
        Assert.Empty(SourceCode.FilesContaining("--password", "OfiFlow.Admin"));
    }

    [Fact]
    public void TheAdminInfrastructure_DoesNotNeedTheKeyThatSignsTheSessions()
    {
        // Esa herramienta no emite sesiones: no debe recibir Jwt:Secret ni poder arrancar con ella. Con solo la cadena de
        // conexión y la clave del segundo factor, el registro funciona y no hay nada que firme tokens.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Server=no-se-usa;Database=no-se-usa",
                ["Mfa:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();

        var services = new ServiceCollection();
        services.AddAdminInfrastructure(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITokenService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IMfaService)); // control positivo: sí registra lo suyo

        // Y la API completa sigue exigiendo Jwt:Secret (control del otro lado).
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(configuration));
    }
}

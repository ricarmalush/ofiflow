using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfiFlow.Admin;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Platform;
using OfiFlow.Infrastructure;

// Programa de administración del servidor (spec 011 R2 y R4, ADR-016). Dos órdenes:
//   create-platform-operator --email <email>    crea un operador con su segundo factor ya confirmado
//   disable-platform-operator --email <email>   lo da de baja
// La contraseña y el código se piden por la terminal; nunca van por argumento.

const int Ok = 0;
const int Failed = 1;
const int Usage = 2;
const int MaxCodeAttempts = 5;

// Los mensajes llevan tildes y la consola de Windows usa por defecto otra página de códigos.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.Length > 0 ? args[0] : null;
var email = Prompt.ReadOption(args, "--email");

if (command is not ("create-platform-operator" or "disable-platform-operator") || string.IsNullOrWhiteSpace(email))
{
    Console.Error.WriteLine("""
        Uso:
          dotnet run --project src/OfiFlow.Admin -- create-platform-operator --email <email>
          dotnet run --project src/OfiFlow.Admin -- disable-platform-operator --email <email>

        Necesita la misma ConnectionStrings:Default y la misma Mfa:EncryptionKey que la API (variables de entorno
        ConnectionStrings__Default y Mfa__EncryptionKey, o user-secrets en local). En local, DOTNET_ENVIRONMENT=Development.
        """);

    return Usage;
}

// ContentRootPath explícito: "dotnet run" no cambia de directorio, y los appsettings viajan junto al programa.
// Sin Args a propósito: no se acepta configuración por la línea de comandos (quedaría en el historial y en la lista de procesos).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });

if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")))
{
    Console.Error.WriteLine("Falta 'ConnectionStrings:Default' (variable de entorno ConnectionStrings__Default). No se ha hecho nada.");
    return Failed;
}

builder.Services.AddSingleton(TimeProvider.System);

try
{
    // Sin Jwt:Secret: esta herramienta no emite sesiones y no debe poder arrancar con la clave que las firma.
    builder.Services.AddAdminInfrastructure(builder.Configuration);
}
catch (InvalidOperationException exception)
{
    // El mensaje nombra la clave que falta o no es válida (Mfa:EncryptionKey), nunca su valor.
    Console.Error.WriteLine(exception.Message);
    return Failed;
}

builder.Services.AddScoped<PlatformOperatorAdministration>();

using var host = builder.Build();

// Después de comprobar la configuración, para que un error de configuración se vea antes de pedir nada. La contraseña se
// escribe a mano en una terminal real: nada de tuberías ni de scripts que la lleven en claro.
if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("Este programa pide la contraseña por la terminal y no funciona con la entrada redirigida.");
    return Failed;
}

return command == "create-platform-operator"
    ? await CreateOperatorAsync(host, email)
    : await DisableOperatorAsync(host, email);

static async Task<int> CreateOperatorAsync(IHost host, string email)
{
    Console.WriteLine($"Alta de un operador de la plataforma: {email}");
    Console.WriteLine($"La contraseña debe tener entre {PasswordRules.MinLength} y {PasswordRules.MaxLength} caracteres. Escape cancela.");

    var password = Prompt.ReadSecret("Contraseña: ");
    if (password is null || password != Prompt.ReadSecret("Repite la contraseña: "))
    {
        Console.Error.WriteLine("Cancelado o las contraseñas no coinciden. No se ha creado nada.");
        return Failed;
    }

    // El secreto solo existe en memoria hasta que el primer código demuestra que la app del móvil lo tiene (R2).
    var secret = TotpSecret.Generate();

    Console.WriteLine();
    Console.WriteLine("Añade esta cuenta a tu app de autenticación (Google Authenticator, Microsoft Authenticator, Aegis...).");
    Console.WriteLine($"  Clave para escribir a mano: {Base32.Encode(secret)}");
    Console.WriteLine($"  O esta dirección:           {TotpSecret.BuildUri(AuthenticationPolicy.TotpIssuer, email, secret)}");
    Console.WriteLine("Hasta que escribas un código válido no se guarda nada.");

    for (var attempt = 1; attempt <= MaxCodeAttempts; attempt++)
    {
        var code = Prompt.ReadLine($"Código de 6 dígitos ({attempt}/{MaxCodeAttempts}): ");

        // Un ámbito por intento: uno que falla deja cambios sin guardar en el contexto.
        using var scope = host.Services.CreateScope();
        var administration = scope.ServiceProvider.GetRequiredService<PlatformOperatorAdministration>();

        var result = await administration.CreateAsync(email, password, secret, code ?? string.Empty, CancellationToken.None);

        switch (result.Outcome)
        {
            case OperatorCreationOutcome.Created:
                Console.WriteLine();
                Console.WriteLine($"Operador creado. Id: {result.UserId}");
                Console.WriteLine("Códigos de recuperación (guárdalos ahora; no se vuelven a mostrar):");
                foreach (var recoveryCode in result.RecoveryCodesInClear)
                {
                    Console.WriteLine($"  {recoveryCode}");
                }

                return Ok;

            case OperatorCreationOutcome.InvalidCode:
                Console.Error.WriteLine("Código no válido. Prueba con el siguiente que muestre la app.");
                break;

            case OperatorCreationOutcome.InvalidEmail:
                Console.Error.WriteLine("El email no es válido. No se ha creado nada.");
                return Failed;

            case OperatorCreationOutcome.InvalidPassword:
                Console.Error.WriteLine($"La contraseña debe tener entre {PasswordRules.MinLength} y {PasswordRules.MaxLength} caracteres. No se ha creado nada.");
                return Failed;

            case OperatorCreationOutcome.EmailAlreadyRegistered:
                Console.Error.WriteLine("Ese email ya está registrado (como empresa o como operador). No se ha modificado nada.");
                return Failed;

            default:
                Console.Error.WriteLine("No se ha podido crear el operador. No se ha guardado nada.");
                return Failed;
        }
    }

    Console.Error.WriteLine("Demasiados códigos erróneos. No se ha creado nada.");
    return Failed;
}

static async Task<int> DisableOperatorAsync(IHost host, string email)
{
    var confirmation = Prompt.ReadLine($"Se dará de baja al operador {email}. Escribe SI para confirmar: ");
    if (confirmation != "SI")
    {
        Console.Error.WriteLine("Cancelado. No se ha modificado nada.");
        return Failed;
    }

    using var scope = host.Services.CreateScope();
    var administration = scope.ServiceProvider.GetRequiredService<PlatformOperatorAdministration>();

    if (await administration.DisableAsync(email, CancellationToken.None) == OperatorDisableOutcome.Disabled)
    {
        Console.WriteLine("Operador dado de baja. Surte efecto en su siguiente petición.");
        return Ok;
    }

    Console.Error.WriteLine("No hay ningún operador activo con ese email. No se ha modificado nada.");
    return Failed;
}

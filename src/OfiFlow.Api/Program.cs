using Microsoft.Extensions.Hosting;
using OfiFlow.Api.Common;
using OfiFlow.Api.Endpoints;
using OfiFlow.Application;
using OfiFlow.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ADR-015: Serilog como un proveedor de logging más, junto a la consola. Sus destinos (sinks) salen
// de la sección "Serilog" de la configuración; sin ella no escribe en ningún sitio. No usa UseSerilog
// a propósito: eso sustituiría el ILoggerFactory y los tests que añaden su propio proveedor dejarían
// de ver los logs. dispose: true vacía el búfer y cierra el fichero al parar la API.
var fileLogger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Logging.AddSerilog(fileLogger, dispose: true);

try
{
    builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // ADR-011: diccionario de mensajes e idioma de la petición.
    builder.Services.AddApiLocalization();

    // ADR-009 R4 y R6.
    builder.Services.AddAuthRateLimiting();
    builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    // El orden importa: las cabeceras de seguridad van primero para cubrir también los errores;
    // el idioma, antes del ExceptionHandler, porque la cultura que fija solo la ven los middlewares
    // posteriores y los errores deben traducirse al idioma de la petición; y el rate limiting antes
    // de la autenticación para no gastar CPU en peticiones que se rechazan.
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseApiLocalization();
    app.UseExceptionHandler();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();

    app.UseMiddleware<RequestLogScopeMiddleware>();
    app.UseRateLimiter();

    // Solo en Development: la pantalla de Swagger lee el documento OpenAPI. Va aquí, después de las
    // cabeceras de seguridad y del manejo de errores, para que sus respuestas también las lleven.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = SecurityHeadersMiddleware.SwaggerUiPath.TrimStart('/');
            options.SwaggerEndpoint("/openapi/v1.json", "OfiFlow API v1");
        });
    }

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapAuthEndpoints();
    app.MapCustomerEndpoints();
    app.MapInvitationEndpoints();
    app.MapUserEndpoints();
    app.MapMfaEndpoints();
    app.MapJobEndpoints();

    app.Run();
}
// HostAbortedException no es un fallo: la lanzan a propósito las herramientas que arrancan la aplicación solo
// para inspeccionarla (dotnet ef, WebApplicationFactory) y deben recibirla intacta.
catch (Exception exception) when (exception is not HostAbortedException)
{
    // ADR-015: un fallo de arranque (falta una clave, el puerto está ocupado...) ocurre antes de que exista
    // ningún manejador de peticiones, así que sin esto solo saldría por la consola y no quedaría en el fichero.
    // El mensaje de estas excepciones nombra la clave que falta, nunca su valor. Se relanza para que el proceso
    // siga terminando con error. No hace falta cerrar el logger: el destino de fichero escribe en cada registro.
    fileLogger.Fatal(exception, "La API no ha podido arrancar");
    throw;
}

// Expuesto para WebApplicationFactory en OfiFlow.Api.Tests.
public partial class Program;

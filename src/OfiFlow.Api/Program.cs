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

    // ADR-017: CORS. El navegador solo deja que una web de otro origen (el frontend) lea las respuestas de la API si la
    // propia API lo autoriza. La autorización es una lista cerrada de orígenes en la configuración ("Cors:AllowedOrigins");
    // sin lista, nadie queda autorizado. Sin comodines ni credenciales de navegador: la sesión viaja en la cabecera
    // Authorization, que una web ajena no puede añadir por su cuenta.
    const string corsOriginsKey = "Cors:AllowedOrigins";
    var allowedOrigins = builder.Configuration.GetSection(corsOriginsKey).Get<string[]>() ?? [];

    foreach (var origin in allowedOrigins)
    {
        // Falla al arrancar, como una clave que falta (ADR-009 R8): un origen mal escrito no debe degradar la protección
        // en silencio. Debe ser lo que envía el navegador en la cabecera Origin (esquema, host y puerto, sin barra final
        // ni ruta), con https salvo en localhost.
        var isValidOrigin = Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                            && originUri.GetLeftPart(UriPartial.Authority) == origin
                            && string.IsNullOrEmpty(originUri.UserInfo)
                            && (originUri.Scheme == Uri.UriSchemeHttps || (originUri.Scheme == Uri.UriSchemeHttp && originUri.IsLoopback));

        if (!isValidOrigin)
        {
            throw new InvalidOperationException(
                $"'{corsOriginsKey}' contiene un origen no válido: '{origin}'. Debe ser esquema, host y puerto sin barra final " +
                "(por ejemplo 'https://app.ejemplo.com'), con https salvo en localhost, y sin comodines.");
        }
    }

    builder.Services.AddCors(options => options.AddPolicy("frontend", policy => policy
        .WithOrigins(allowedOrigins)
        // Solo los verbos que usa la API y las cabeceras que el cliente envía de verdad (sesión, cuerpo JSON, idioma).
        .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete)
        .WithHeaders("Authorization", "Content-Type", "Accept-Language")
        // Sin esto el navegador esconde Retry-After al JavaScript y el cliente no sabría cuánto esperar tras un 429.
        .WithExposedHeaders("Retry-After")
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

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

    // CORS antes del rate limiting y de la autenticación: la petición previa (OPTIONS) la responde él mismo sin pasar
    // por ellas, y un 429 también lleva las cabeceras, porque si no el navegador se lo escondería al frontend.
    app.UseCors("frontend");

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
    app.MapPlatformEndpoints();
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

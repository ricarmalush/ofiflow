using OfiFlow.Api.Common;
using OfiFlow.Api.Endpoints;
using OfiFlow.Application;
using OfiFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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

// Expuesto para WebApplicationFactory en OfiFlow.Api.Tests.
public partial class Program;

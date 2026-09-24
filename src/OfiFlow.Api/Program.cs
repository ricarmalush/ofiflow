using OfiFlow.Api.Common;
using OfiFlow.Api.Endpoints;
using OfiFlow.Application;
using OfiFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ADR-009 R4 y R6.
builder.Services.AddAuthRateLimiting();
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// El orden importa: las cabeceras de seguridad van primero para cubrir también los errores,
// y el rate limiting antes de la autenticación para no gastar CPU en peticiones que se rechazan.
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseMiddleware<RequestLogScopeMiddleware>();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapCustomerEndpoints();
app.MapJobEndpoints();

app.Run();

// Expuesto para WebApplicationFactory en OfiFlow.Api.Tests.
public partial class Program;

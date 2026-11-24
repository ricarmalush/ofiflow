using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfiFlow.Application.Common.Behaviors;

namespace OfiFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            // El orden de registro es el de ejecución: la autorización va primero (ADR-012 R3).
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Lee presupuesto, cliente y perfil y genera el PDF: lo comparten la descarga (spec 025) y el envío por correo (spec 026 D5).
        services.AddScoped<Quotes.QuotePdfBuilder>();

        // La hora entra por TimeProvider (no por DateTime.UtcNow) para poder probar la caducidad sin esperar.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}

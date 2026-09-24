using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Persistence.Interceptors;
using OfiFlow.Infrastructure.Tenancy;

namespace OfiFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlServer(configuration.GetConnectionString(ConnectionStringNames.Default));
            options.AddInterceptors(new AuditableEntitySaveChangesInterceptor());
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, TenantContext>();

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<ITokenService, TokenService>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        AddJwtAuthentication(services, configuration);

        return services;
    }

    private static void AddJwtAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        // Sin valor por defecto deliberadamente: si "Jwt:Secret" no está configurado, debe
        // fallar alto y claro al arrancar, no arrancar de forma insegura con una clave débil.
        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Falta la sección de configuración 'Jwt'.");

        if (string.IsNullOrWhiteSpace(jwtOptions.Secret))
        {
            throw new InvalidOperationException(
                "Falta 'Jwt:Secret'. En local: dotnet user-secrets set \"Jwt:Secret\" \"<base64 de 32 bytes>\" " +
                "--project src/OfiFlow.Api. En producción: variable de entorno Jwt__Secret o Key Vault (ADR-009 R8). " +
                DescribeSecretSources(configuration));
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtOptions.Secret)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorization();
    }

    /// <summary>
    /// Para diagnosticar un secreto que no llega: qué fuentes de configuración se consultaron y
    /// cuáles tienen 'Jwt:Secret'. Solo muestra la longitud, nunca el valor.
    /// </summary>
    private static string DescribeSecretSources(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return string.Empty;
        }

        var sources = root.Providers.Select(provider =>
        {
            var found = provider.TryGet($"{JwtOptions.SectionName}:{nameof(JwtOptions.Secret)}", out var value);
            var file = provider is FileConfigurationProvider fileProvider
                ? fileProvider.Source.FileProvider?.GetFileInfo(fileProvider.Source.Path ?? string.Empty)
                : null;
            var location = file is null ? string.Empty : $" [{file.PhysicalPath}, existe: {file.Exists}]";
            return $"{provider}{location} → {(found ? $"{value?.Length ?? 0} caracteres" : "no lo tiene")}";
        });

        var entry = Assembly.GetEntryAssembly();
        var secretsId = entry?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
        var secretsPath = secretsId is null ? null : PathHelper.GetSecretsPathFromSecretsId(secretsId);
        var userSecrets = $"Ensamblado de entrada: {entry?.GetName().Name ?? "(ninguno)"}, UserSecretsId: {secretsId ?? "(ninguno)"}, " +
            $"ruta esperada: {secretsPath ?? "-"}, carpeta existe: {(secretsPath is null ? "-" : Directory.Exists(Path.GetDirectoryName(secretsPath)).ToString())}";

        return "Fuentes consultadas: " + string.Join(" | ", sources) + ". " + userSecrets;
    }
}

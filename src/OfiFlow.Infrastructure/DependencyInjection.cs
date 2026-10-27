using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Persistence.Interceptors;
using OfiFlow.Infrastructure.Tenancy;

namespace OfiFlow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Todo lo que necesita la API: el almacén de cuentas y, además, las sesiones (JWT).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // La clave JWT se valida primero, como siempre: si faltan las dos, el mensaje es el de Jwt:Secret.
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        AddJwtAuthentication(services, configuration);
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IPlatformTokenService, PlatformTokenService>();

        return services.AddAccountStore(configuration);
    }

    /// <summary>
    /// Lo que necesita el programa de administración del servidor (spec 011 R2): el almacén de cuentas, el segundo factor y
    /// la plataforma, <b>sin</b> la autenticación JWT. A propósito: la clave que firma las sesiones (<c>Jwt:Secret</c>) es el
    /// secreto más delicado y esa herramienta no emite sesiones, así que no debe recibirla ni poder arrancar con ella. Sí
    /// necesita <c>Mfa:EncryptionKey</c>, y debe ser <b>la misma que usa la API</b>: con otra, la API no podría descifrar
    /// el secreto del operador y no podría iniciar sesión.
    /// </summary>
    public static IServiceCollection AddAdminInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        return services.AddAccountStore(configuration);
    }

    private static IServiceCollection AddAccountStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlServer(configuration.GetConnectionString(ConnectionStringNames.Default));
            options.AddInterceptors(new AuditableEntitySaveChangesInterceptor());
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentUser, CurrentUser>();

        // El verificador de contraseñas, explícito: los tests lo sustituyen por uno que cuenta cada comprobación (spec 010 R18).
        services.AddSingleton<IPasswordChecker>(PasswordHasherChecker.Instance);

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IPlatformOperators, PlatformOperators>();
        services.AddScoped<IPlatformAudit, PlatformAudit>();

        AddMfaEncryption(services, configuration);

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

        // Spec 011 R7: la plataforma es OTRO mundo, con su clave y su audiencia, y tampoco tiene valor por defecto: la API no
        // arranca si falta, si no mide 32 bytes o si repite la clave o la audiencia de las empresas.
        var platformKey = PlatformAuthentication.ParseKey(jwtOptions);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Conserva los nombres de claim tal como los escribe TokenService ("sub", "role", "tenant_id").
                // Por defecto JwtBearer los renombra al validar (role -> ClaimTypes.Role, sub -> NameIdentifier) y
                // CurrentUser no encontraría el rol: el Owner recibiría un 403 en todo (ADR-012, spec 008).
                options.MapInboundClaims = false;

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
            })
            // El esquema de la plataforma: otra clave y otra audiencia. Un token de empresa no pasa aquí y uno de plataforma no
            // pasa en el esquema por defecto (el de las empresas), aunque la firma sea correcta en su propio mundo.
            .AddJwtBearer(PlatformAuthentication.Scheme, options =>
            {
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.PlatformAudience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(platformKey),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

        // La política de las rutas de /api/v1/platform: solo vale una sesión del esquema de plataforma, y además con el claim del
        // mundo de plataforma (defensa en profundidad: un token firmado con la clave de plataforma pero sin él tampoco sirve).
        services.AddAuthorization(options => options.AddPolicy(PlatformAuthentication.Policy, policy => policy
            .AddAuthenticationSchemes(PlatformAuthentication.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(OfiFlowClaimTypes.Realm, OfiFlowClaimTypes.PlatformRealm)));
    }

    private static void AddMfaEncryption(IServiceCollection services, IConfiguration configuration)
    {
        // Igual que Jwt:Secret: sin valor por defecto. Si la clave falta o es inválida, la API no arranca:
        // arrancar sin ella dejaría guardar secretos sin poder cifrarlos, o sin poder leer los ya guardados.
        var options = configuration.GetSection(MfaOptions.SectionName).Get<MfaOptions>() ?? new MfaOptions();
        var key = MfaOptions.ParseKey(options.EncryptionKey);

        services.Configure<MfaOptions>(configuration.GetSection(MfaOptions.SectionName));

        // Un único protector para toda la aplicación: no tiene estado y la clave no cambia mientras corre.
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(key));
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

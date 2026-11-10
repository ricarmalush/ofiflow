using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Infrastructure.Documents;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Legal;
using OfiFlow.Infrastructure.Mail;
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

        // Una sola instancia por petición, vista por sus dos contratos: el de Application (ITokenService) y el de la purga (IRefreshTokenPurger,
        // interno: ningún caso de uso lo necesita). TokenService sigue siendo el único fichero que lee la tabla de refresh tokens.
        // Lo que se guarda del cliente al iniciar sesión (spec 016): navegador y red truncada, saneados. Solo lo lee TokenService.
        services.AddScoped<IClientInfo, ClientInfo>();
        services.AddScoped<TokenService>();
        services.AddScoped<ITokenService>(provider => provider.GetRequiredService<TokenService>());
        services.AddScoped<IRefreshTokenPurger>(provider => provider.GetRequiredService<TokenService>());

        // Los tokens de restablecimiento de contraseña (spec 018): PasswordResetService es el único fichero que lee su tabla. Por ahora solo se ve como el purgador;
        // pedir y consumir un token llegan con los casos de uso.
        services.AddScoped<PasswordResetService>();
        services.AddScoped<IPasswordResetTokenPurger>(provider => provider.GetRequiredService<PasswordResetService>());
        services.AddScoped<IPasswordResetService>(provider => provider.GetRequiredService<PasswordResetService>());

        // Los registros pendientes de confirmar (spec 021): PendingRegistrationService es el único fichero que lee su tabla. Un solo objeto por petición visto por sus dos contratos: el de
        // Application (pedir y canjear) y el de la purga (interno).
        services.AddScoped<PendingRegistrationService>();
        services.AddScoped<IPendingRegistrationService>(provider => provider.GetRequiredService<PendingRegistrationService>());
        services.AddScoped<IPendingRegistrationPurger>(provider => provider.GetRequiredService<PendingRegistrationService>());

        // El límite de avisos «ya tienes una cuenta» por email (R7): en memoria, así que UNA instancia para toda la aplicación (si fuera scoped, cada petición empezaría de cero).
        services.AddSingleton<IRegistrationNoticeThrottle, RegistrationNoticeThrottle>();

        // La versión vigente de las condiciones de uso (spec 021 R8, D7): validada al arrancar. Vacía, el registro está cerrado.
        services.AddOptions<LegalOptions>()
            .Bind(configuration.GetSection(LegalOptions.SectionName))
            .Validate(options => options.Validate() is null, "La sección Legal no vale: revisa TermsVersion (una etiqueta corta, como 2026-11).")
            .ValidateOnStart();
        services.AddSingleton<ILegalTerms, LegalTerms>();
        services.AddScoped<IPlatformTokenService, PlatformTokenService>();

        return services.AddAccountStore(configuration);
    }

    /// <summary>
    /// El servicio en segundo plano que purga los refresh tokens caducados (spec 016, ADR-019). Lo llama <b>solo la API</b>: la consola de
    /// administración es interactiva y de un solo uso, y no debe lanzar tareas periódicas. La configuración se valida al arrancar: un valor que
    /// no vale (intervalo de cero, margen de cero días...) impide arrancar en vez de fallar luego en silencio.
    /// </summary>
    public static IServiceCollection AddRefreshTokenPurge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RefreshTokenPurgeOptions>()
            .Bind(configuration.GetSection(RefreshTokenPurgeOptions.SectionName))
            .Validate(options => options.Validate() is null, "La sección RefreshTokenPurge no vale: revisa Interval, StartupDelay, PauseBetweenBatches, RetentionDays y BatchSize.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<RefreshTokenPurgeService>();

        return services;
    }

    /// <summary>
    /// El correo (spec 017, ADR-020). Lo llama <b>solo la API</b>: la consola de administración no envía correo. La configuración se valida al arrancar (R8). Con
    /// <c>Email:Provider = None</c> (por defecto) se registra un transporte que falla en voz alta si algo lo usa; con <c>Smtp</c>, el real.
    /// </summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IEmailLinks, EmailLinks>();

        // La cola de correo en memoria y la tarea que la vacía (spec 018, D3, ADR-021): para los correos que no pueden esperar al servidor de correo dentro de la petición.
        // Opciones validadas al arrancar; solo en la API (la consola de administración no recibe AddEmail).
        services.AddOptions<EmailDispatchOptions>()
            .Bind(configuration.GetSection(EmailDispatchOptions.SectionName))
            .Validate(options => options.Validate() is null, "La sección EmailDispatch no vale: revisa QueueCapacity (entre 1 y 10000).")
            .ValidateOnStart();
        services.AddSingleton<EmailQueue>();
        services.AddSingleton<IEmailQueue>(provider => provider.GetRequiredService<EmailQueue>());
        services.AddHostedService<EmailDispatchService>();

        // Se lee la opción ya enlazada para decidir el transporte; la validación completa la hace el arranque.
        var isSmtp = Enum.TryParse<EmailProvider>(configuration.GetSection(EmailOptions.SectionName)[nameof(EmailOptions.Provider)], ignoreCase: true, out var provider) && provider == EmailProvider.Smtp;
        if (isSmtp)
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, NullEmailSender>();
        }

        return services;
    }

    /// <summary>
    /// El PDF del presupuesto (spec 025, ADR-028). Lo llama <b>solo la API</b>. Un solo generador para toda la aplicación: no guarda estado (cada llamada construye su documento) y se puede usar desde
    /// varias peticiones a la vez. Las fuentes van dentro del ensamblado, así que no hay nada que configurar ni nada del sistema de ficheros del que depender.
    /// </summary>
    public static IServiceCollection AddQuotePdf(this IServiceCollection services)
    {
        services.AddSingleton<IQuotePdfRenderer, PdfSharpQuotePdfRenderer>();

        return services;
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

        // A quién se obliga el segundo factor (spec 022): la regla de Domain, sin más. SIEMPRE esta implementación en producción; solo los tests pueden sustituirla.
        services.AddSingleton<ISecondFactorRequirement, SecondFactorRequirement>();

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

                // Spec 015: además de la firma y la caducidad, la sesión del token debe seguir activa. Un fallo es un 401.
                options.Events = new JwtBearerEvents { OnTokenValidated = SessionTokenValidation.ValidateAsync };
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

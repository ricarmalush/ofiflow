using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Mail;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// La configuración del correo (spec 017, R3–R8) y su registro. Como las demás secciones (<c>RefreshTokenPurge</c>, <c>Cors</c>), una configuración que no
/// vale impide arrancar en vez de fallar al primer envío; y ningún mensaje repite un valor, porque podría ser la contraseña.
/// </summary>
public class EmailOptionsTests
{
    private const string Secret = "contraseña-secretísima-1234";
    private const string SmtpUser = "smtp-user-q7x";

    private static EmailOptions ValidSmtp() => new()
    {
        Provider = EmailProvider.Smtp,
        From = "no-reply@ofiflow.example",
        LinkBaseUrl = "https://app.ofiflow.example",
        Smtp = new SmtpOptions { Host = "smtp.ofiflow.example", Port = 587, UserName = SmtpUser, Password = Secret }
    };

    private static EmailOptions Broken(Action<EmailOptions> mutate)
    {
        var options = ValidSmtp();
        mutate(options);
        return options;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // ===== Valores por defecto y configuraciones válidas =====

    [Fact]
    public void Defaults_AreTheApprovedOnes_AndAreValid()
    {
        var options = new EmailOptions();

        Assert.Equal(EmailProvider.None, options.Provider);
        Assert.False(options.IsEnabled);
        Assert.Equal("OfiFlow", options.FromName);
        Assert.Equal(587, options.Smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, options.Smtp.Security);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Smtp.Timeout);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void AValidSmtpConfiguration_IsValid_AndIsEnabled()
    {
        var options = ValidSmtp();

        Assert.Null(options.Validate());
        Assert.True(options.IsEnabled);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("LOCALHOST")]
    public void ASmtpServerOnThisMachine_MayBeUsedWithoutEncryption(string host)
    {
        var options = Broken(o =>
        {
            o.Smtp.Host = host;
            o.Smtp.Security = SmtpSecurity.None;
            o.Smtp.UserName = null;
            o.Smtp.Password = null;
        });

        Assert.Null(options.Validate());
    }

    [Fact]
    public void SmtpWithoutCredentials_IsValid_ForServersThatDoNotAskForThem()
    {
        var options = Broken(o =>
        {
            o.Smtp.UserName = null;
            o.Smtp.Password = null;
        });

        Assert.Null(options.Validate());
    }

    [Theory]
    [InlineData("https://app.ofiflow.example")]
    [InlineData("https://app.ofiflow.example:8443")]
    [InlineData("http://localhost:4200")]
    [InlineData("http://127.0.0.1:4200")]
    [InlineData("http://[::1]:4200")]
    public void ALinkBaseUrl_ThatIsAnExactOrigin_IsValid(string url)
    {
        Assert.Null(Broken(o => o.LinkBaseUrl = url).Validate());
    }

    // ===== Cada valor inválido, rechazado =====

    [Theory]
    [InlineData("http://app.ofiflow.example")]                 // http fuera de localhost
    [InlineData("https://app.ofiflow.example/")]               // barra final
    [InlineData("https://app.ofiflow.example/ruta")]           // con ruta
    [InlineData("https://app.ofiflow.example?x=1")]            // con parámetros
    [InlineData("https://app.ofiflow.example#token")]          // con fragmento
    [InlineData("https://usuario:clave@app.ofiflow.example")]  // con usuario
    [InlineData("ftp://app.ofiflow.example")]                  // otro esquema
    [InlineData("app.ofiflow.example")]                        // sin esquema
    [InlineData("https://app.ofiflow.example\r\nBcc: x@y.z")]  // con salto de línea
    [InlineData("https://app ofiflow.example")]                // con espacio
    public void ALinkBaseUrl_ThatIsNotAnExactOrigin_IsRejected_EvenWhenEmailIsOff(string url)
    {
        // Se valida siempre que exista: un valor mal escrito que hoy no se usa fallaría el día que se active el correo.
        var options = new EmailOptions { LinkBaseUrl = url };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void WithSmtp_ALinkBaseUrlIsRequired()
    {
        Assert.Contains("LinkBaseUrl", Broken(o => o.LinkBaseUrl = null).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]                                   // sin punto en el dominio
    [InlineData("Nombre <a@ofiflow.example>")]            // con nombre y ángulos
    [InlineData("a@ofiflow.example, b@ofiflow.example")]  // dos direcciones
    [InlineData("a@ofiflow.example\r\nBcc: b@c.d")]       // inyección de cabecera
    [InlineData("a b@ofiflow.example")]
    [InlineData("a@@ofiflow.example")]
    public void WithSmtp_FromMustBeAPlainAddress(string? from)
    {
        Assert.Contains("From", Broken(o => o.From = from).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ofi\r\nFlow")]
    [InlineData("Ofi\tFlow")]
    public void WithSmtp_FromNameCannotBeEmptyNorHaveControlCharacters(string name)
    {
        Assert.Contains("FromName", Broken(o => o.FromName = name).Validate());
    }

    [Fact]
    public void WithSmtp_FromNameCannotBeTooLong()
    {
        Assert.Contains("FromName", Broken(o => o.FromName = new string('a', EmailOptions.MaxFromNameLength + 1)).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("smtp ofiflow.example")]
    [InlineData("smtp.ofiflow.example\r\n")]
    public void WithSmtp_AHostIsRequired(string? host)
    {
        Assert.Contains("Host", Broken(o => o.Smtp.Host = host).Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void WithSmtp_ThePortMustBeAValidPort(int port)
    {
        Assert.Contains("Port", Broken(o => o.Smtp.Port = port).Validate());
    }

    [Fact]
    public void WithSmtp_AnUnknownSecurityMode_IsRejected()
    {
        Assert.Contains("Security", Broken(o => o.Smtp.Security = (SmtpSecurity)42).Validate());
    }

    [Fact]
    public void NoEncryption_IsRejected_ForAServerThatIsNotOnThisMachine()
    {
        // R5: el correo y las credenciales no pueden viajar sin cifrar por una red.
        Assert.Contains("Security", Broken(o => o.Smtp.Security = SmtpSecurity.None).Validate());
    }

    [Theory]
    [InlineData("localhost.ofiflow.example")]   // parece localhost pero no lo es
    [InlineData("127.0.0.1.ofiflow.example")]
    [InlineData("10.0.0.5")]                    // red privada: no es esta máquina
    public void NoEncryption_IsRejected_ForHostsThatOnlyLookLocal(string host)
    {
        var options = Broken(o =>
        {
            o.Smtp.Host = host;
            o.Smtp.Security = SmtpSecurity.None;
        });

        Assert.Contains("Security", options.Validate());
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    [InlineData("00:01:01")]
    public void WithSmtp_TheTimeoutMustBePositive_AndNotAbsurdlyLong(string timeout)
    {
        Assert.Contains("Timeout", Broken(o => o.Smtp.Timeout = TimeSpan.Parse(timeout)).Validate());
    }

    [Fact]
    public void WithSmtp_TheTimeoutMayBeAsLongAsTheMaximum()
    {
        Assert.Null(Broken(o => o.Smtp.Timeout = EmailOptions.MaxTimeout).Validate());
    }

    [Theory]
    [InlineData(SmtpUser, null)]
    [InlineData(SmtpUser, "")]
    [InlineData(null, Secret)]
    [InlineData("", Secret)]
    public void WithSmtp_UserNameAndPasswordGoTogether(string? userName, string? password)
    {
        var options = Broken(o =>
        {
            o.Smtp.UserName = userName;
            o.Smtp.Password = password;
        });

        Assert.Contains("UserName", options.Validate());
    }

    [Fact]
    public void NoValidationMessage_EchoesAValue_NotEvenThePassword()
    {
        // Cada camino de fallo, con la contraseña presente en la configuración.
        var brokenOnes = new[]
        {
            Broken(o => o.LinkBaseUrl = "http://x.example/" + Secret),
            Broken(o => o.From = "x" + Secret),
            Broken(o => o.FromName = "a\r\n" + Secret),
            Broken(o => o.Smtp.Host = "a b" + Secret),
            Broken(o => o.Smtp.Port = 0),
            Broken(o => o.Smtp.Security = SmtpSecurity.None),
            Broken(o => o.Smtp.Timeout = TimeSpan.Zero),
            Broken(o => o.Smtp.UserName = null)
        };

        foreach (var options in brokenOnes)
        {
            var message = options.Validate();
            Assert.NotNull(message);
            Assert.DoesNotContain(Secret, message);
            Assert.DoesNotContain(SmtpUser, message);
        }
    }

    // ===== El registro =====

    [Fact]
    public void AddEmail_WithoutASection_RegistersTheTransportThatFailsLoudly()
    {
        using var provider = new ServiceCollection().AddEmail(Config()).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
        Assert.False(options.IsEnabled);
        Assert.Equal("NullEmailSender", provider.GetRequiredService<IEmailSender>().GetType().Name);
        Assert.False(provider.GetRequiredService<IEmailSender>().IsEnabled);
    }

    [Fact]
    public async Task TheNullTransport_ThrowsIfAnythingCallsIt_InsteadOfPretendingToSend()
    {
        using var provider = new ServiceCollection().AddEmail(Config()).BuildServiceProvider();
        var message = new EmailMessage(Email.Create("alguien@ejemplo.com"), "Asunto", "Cuerpo");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IEmailSender>().SendAsync(message, CancellationToken.None));
    }

    [Fact]
    public void AddEmail_WithSmtp_BindsTheSection_AndRegistersTheSmtpTransport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmail(Config(
            ("Email:Provider", "Smtp"), ("Email:From", "no-reply@ofiflow.example"), ("Email:FromName", "Mi Empresa"), ("Email:LinkBaseUrl", "https://app.ofiflow.example"),
            ("Email:Smtp:Host", "smtp.ofiflow.example"), ("Email:Smtp:Port", "465"), ("Email:Smtp:Security", "SslOnConnect"), ("Email:Smtp:Timeout", "00:00:05"),
            ("Email:Smtp:UserName", "u"), ("Email:Smtp:Password", Secret)));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
        Assert.Equal(EmailProvider.Smtp, options.Provider);
        Assert.Equal("Mi Empresa", options.FromName);
        Assert.Equal(465, options.Smtp.Port);
        Assert.Equal(SmtpSecurity.SslOnConnect, options.Smtp.Security);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Smtp.Timeout);
        Assert.Equal("SmtpEmailSender", provider.GetRequiredService<IEmailSender>().GetType().Name);
        Assert.True(provider.GetRequiredService<IEmailSender>().IsEnabled);
        Assert.StartsWith("https://app.ofiflow.example/auth/accept-invitation#", provider.GetRequiredService<IEmailLinks>().AcceptInvitation("t"));
    }

    [Fact]
    public void AddEmail_WithAnInvalidSection_FailsWhenTheOptionsAreRead_NotLaterInSilence()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmail(Config(("Email:Provider", "Smtp"), ("Email:From", "no-reply@ofiflow.example"), ("Email:Smtp:Host", "smtp.ofiflow.example")));
        using var provider = services.BuildServiceProvider();

        // Falta LinkBaseUrl: el error dice QUÉ ajuste falla, no un aviso genérico.
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<EmailOptions>>().Value);
        Assert.Contains("LinkBaseUrl", exception.Message);
        Assert.Contains("LinkBaseUrl", Assert.Single(exception.Failures));
    }

    [Fact]
    public async Task AnInvalidEmailSection_StopsTheHostFromStarting()
    {
        // R8, de punta a punta: con el host de verdad, una configuración que no vale impide ARRANCAR (ValidateOnStart), no solo falla al leer la opción.
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Provider"] = "Smtp", ["Email:From"] = "no-reply@ofiflow.example", ["Email:Smtp:Host"] = "smtp.ofiflow.example" });
        builder.Services.AddLogging();
        builder.Services.AddEmail(builder.Configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("LinkBaseUrl", exception.Message); // y dice qué ajuste
    }

    [Fact]
    public async Task AValidEmailSection_LetsTheHostStart()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddLogging();
        builder.Services.AddEmail(builder.Configuration); // sin sección: correo apagado, válido
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void AddEmail_WithAnUnknownProvider_FailsWhenTheOptionsAreRead()
    {
        // Un error de escritura («Smpt») no puede dejar el correo apagado en silencio.
        var services = new ServiceCollection();
        services.AddEmail(Config(("Email:Provider", "Smpt")));
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IOptions<EmailOptions>>().Value);
    }

    [Fact]
    public void TheAdminConsole_NeverGetsTheEmailTransport()
    {
        // La consola de administración no envía correo (spec 017, tarea del bloque 1).
        var services = new ServiceCollection();
        services.AddAdminInfrastructure(Config(
            ("ConnectionStrings:Default", "Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True"),
            ("Mfa:EncryptionKey", Convert.ToBase64String(new byte[32]))));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEmailSender));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEmailLinks));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOptions<EmailOptions>));
    }

    // ===== El aviso de arranque (D5) =====

    private static (ListLogger<EmailOptionsTests> Log, int Warnings) Warn(string environment, EmailProvider provider)
    {
        var log = new ListLogger<EmailOptionsTests>();
        var options = Options.Create(new EmailOptions { Provider = provider });
        EmailStartupCheck.WarnIfOffInProduction(new FakeEnvironment(environment), options, log);
        return (log, log.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void InProductionWithoutEmail_ItWarns_WithoutStoppingAnything()
    {
        var (log, warnings) = Warn("Production", EmailProvider.None);

        Assert.Equal(1, warnings);
        Assert.Contains("Email:Provider = None", Assert.Single(log.Entries).Message);
    }

    [Theory]
    [InlineData("Development", EmailProvider.None)]
    [InlineData("Testing", EmailProvider.None)]
    [InlineData("Staging", EmailProvider.None)]
    [InlineData("Production", EmailProvider.Smtp)]
    public void Elsewhere_OrWithEmailOn_ItDoesNotWarn(string environment, EmailProvider provider)
    {
        Assert.Equal(0, Warn(environment, provider).Warnings);
    }

    // ===== La excepción =====

    [Theory]
    [InlineData(EmailFailureCategory.Timeout)]
    [InlineData(EmailFailureCategory.Authentication)]
    [InlineData(EmailFailureCategory.Rejected)]
    [InlineData(EmailFailureCategory.Unavailable)]
    [InlineData(EmailFailureCategory.Unknown)]
    public void TheDeliveryException_HasAFixedMessagePerCategory_AndKeepsNoInnerException(EmailFailureCategory category)
    {
        var exception = new EmailDeliveryException(category);

        Assert.Equal(category, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("@", exception.Message);
    }

    [Fact]
    public void EveryCategory_HasItsOwnMessage()
    {
        var messages = Enum.GetValues<EmailFailureCategory>().Select(c => new EmailDeliveryException(c).Message).ToList();

        Assert.Equal(messages.Count, messages.Distinct().Count());
    }
}

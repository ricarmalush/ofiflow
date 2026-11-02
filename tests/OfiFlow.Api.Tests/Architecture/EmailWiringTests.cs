using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure.Mail;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// El cableado del correo (spec 017, bloque 1) y sus límites. El comportamiento del transporte lo prueban los tests de Infrastructure contra un servidor SMTP;
/// aquí se protege lo que ninguno de ellos ve: que la API de verdad registra el puerto (si alguien borrara la línea de <c>Program.cs</c>, un caso de uso que
/// pida <see cref="IEmailSender"/> fallaría al primer envío y no al arrancar), que la consola de administración no lo recibe, y que la biblioteca de correo
/// no se cuela fuera de su carpeta (ADR-020: el resto del código solo conoce el puerto).
/// </summary>
public class EmailWiringTests(OfiFlowApiFactory factory) : IClassFixture<OfiFlowApiFactory>
{
    private const string MailFolder = "src/OfiFlow.Infrastructure/Mail/";

    [Fact]
    public void TheApiHost_RegistersTheEmailPort_AndByDefaultSendsNothing()
    {
        // Sin sección Email en la configuración de los tests: el correo está apagado (D5) y el transporte registrado es el que falla en voz alta.
        var options = factory.Services.GetRequiredService<IOptions<EmailOptions>>().Value;

        Assert.False(options.IsEnabled);
        Assert.Equal("NullEmailSender", factory.Services.GetRequiredService<IEmailSender>().GetType().Name);
    }

    [Fact]
    public void ProgramCs_RegistersTheEmail_AndTheAdminProgramDoesNot()
    {
        Assert.Contains("src/OfiFlow.Api/Program.cs", SourceCode.FilesContaining(".AddEmail(", "OfiFlow.Api"));
        Assert.Empty(SourceCode.FilesContaining(".AddEmail(", "OfiFlow.Admin"));
        Assert.Empty(SourceCode.FilesContaining("IEmailSender", "OfiFlow.Admin"));
    }

    [Fact]
    public void TheApiHost_RegistersTheEmailQueue_AndItsDispatchService()
    {
        // Spec 018: sin el servicio, un correo encolado no saldría nunca y nada fallaría a la vista; sin la cola, un caso de uso que la pida fallaría al primer uso.
        var queue = factory.Services.GetRequiredService<IEmailQueue>();

        Assert.Same(queue, factory.Services.GetRequiredService<IEmailQueue>()); // un singleton
        Assert.Contains(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), service => service.GetType().Name == "EmailDispatchService");
        Assert.True(factory.Services.GetRequiredService<IOptions<EmailDispatchOptions>>().Value.Enabled);
    }

    [Fact]
    public void TheAdminProgram_DoesNotUseTheEmailQueue()
    {
        Assert.Empty(SourceCode.FilesContaining("IEmailQueue", "OfiFlow.Admin"));
        Assert.Empty(SourceCode.FilesContaining("EmailDispatch", "OfiFlow.Admin"));
    }

    [Fact]
    public void TheQueueSideThatReads_IsOnlyUsedByTheDispatchService()
    {
        // Application solo encola (IEmailQueue). Quien lee del canal es el servicio de envío: si otro fichero leyera, dos lectores se repartirían los mensajes.
        var readers = SourceCode.Files()
            .Where(file => file.Content.Contains(".Reader", StringComparison.Ordinal) && file.Content.Contains("EmailQueue", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Order()
            .ToList();

        Assert.Equal([MailFolder + "EmailDispatchService.cs", MailFolder + "EmailQueue.cs"], readers);
    }

    [Fact]
    public void ProgramCs_RunsTheProductionStartupCheck()
    {
        // D5: si alguien borrara la llamada, el aviso desaparecería sin que fallara nada.
        Assert.Contains("src/OfiFlow.Api/Program.cs", SourceCode.FilesContaining("EmailStartupCheck.WarnIfOffInProduction(", "OfiFlow.Api"));
    }

    [Fact]
    public void InvitationLinks_AreBuiltInOnePlaceOnly_FromConfiguration()
    {
        // R3: el único sitio que escribe la ruta del enlace es EmailLinks, que solo lee Email:LinkBaseUrl. Si otro fichero lo hiciera podría construirlo con datos de la petición.
        // Se busca el fragmento con el token («#token=»), que es lo que tiene en común todo enlace con un secreto (invitación y restablecimiento de contraseña, spec 018):
        // así un enlace nuevo no puede escribirse en otro sitio sin que este test lo vea.
        var builders = SourceCode.Files()
            .Where(file => file.Content.Split('\n').Select(line => line.TrimStart()).Any(line => !line.StartsWith("//", StringComparison.Ordinal) && line.Contains("#token=", StringComparison.Ordinal)))
            .Select(file => file.Path)
            .ToList();
        Assert.Equal([MailFolder + "EmailLinks.cs"], builders);

        // Y las dos rutas de los enlaces solo las nombra ese fichero (en código: los comentarios y las rutas de los endpoints, que son relativas, no cuentan).
        foreach (var path in new[] { "\"/auth/accept-invitation\"", "\"/auth/reset-password\"" })
        {
            var namers = SourceCode.Files()
                .Where(file => file.Content.Split('\n').Select(line => line.TrimStart()).Any(line => !line.StartsWith("//", StringComparison.Ordinal) && line.Contains(path, StringComparison.Ordinal)))
                .Select(file => file.Path)
                .ToList();
            Assert.Equal([MailFolder + "EmailLinks.cs"], namers);
        }

        // Y ningún fichero que toque correo o invitaciones lee cabeceras que controla quien envía la petición.
        var risky = SourceCode.Files()
            .Where(file => file.Path.Contains("/Mail/", StringComparison.Ordinal) || file.Path.Contains("InviteUser", StringComparison.Ordinal) || file.Path.Contains("InvitationEmail", StringComparison.Ordinal))
            .Where(file => new[] { "Request.Host", "HeaderNames.Origin", "HeaderNames.Referer", "X-Forwarded-Host", "Request.Headers" }.Any(text => file.Content.Contains(text, StringComparison.Ordinal)))
            .Select(file => file.Path)
            .ToList();
        Assert.Empty(risky);
    }

    [Fact]
    public void TheMailLibraries_AreOnlyUsedInsideTheMailFolder()
    {
        // El resto del código (casos de uso incluidos) solo conoce IEmailSender. Si otro fichero usara MailKit o MimeKit directamente, se saltaría las garantías
        // del transporte: cifrado obligatorio, tiempo máximo, errores sin datos personales.
        var users = SourceCode.Files()
            .Where(file => file.Content.Contains("using MailKit", StringComparison.Ordinal) || file.Content.Contains("using MimeKit", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        Assert.NotEmpty(users); // control positivo: el transporte sí las usa
        Assert.All(users, path => Assert.StartsWith(MailFolder, path, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheMailFolder_ConstructsTheSmtpTransport()
    {
        // Los casos de uso piden el puerto por inyección; nadie construye el transporte a mano.
        var constructors = SourceCode.FilesContaining("new SmtpEmailSender(");

        Assert.All(constructors, path => Assert.StartsWith(MailFolder, path, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCertificateValidation_IsNeverTurnedOffInTheTransport()
    {
        // R5: la validación normal del certificado. El único gancho es el constructor de tests, que recibe la función desde fuera.
        var sender = SourceCode.ReadText(MailFolder + "SmtpEmailSender.cs");

        // El único gancho es el que llega por el constructor de tests; nadie escribe una función propia que acepte todo.
        Assert.Equal(1, sender.Split("ServerCertificateValidationCallback").Length - 1);
        Assert.Contains("ServerCertificateValidationCallback = _certificateValidation;", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("SslProtocols", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureSocketOptions.Auto", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTlsWhenAvailable", sender, StringComparison.Ordinal);
    }
}

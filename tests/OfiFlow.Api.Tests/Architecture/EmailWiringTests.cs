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

        Assert.DoesNotContain("=> true", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("SslProtocols", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("SecureSocketOptions.Auto", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("StartTlsWhenAvailable", sender, StringComparison.Ordinal);
    }
}

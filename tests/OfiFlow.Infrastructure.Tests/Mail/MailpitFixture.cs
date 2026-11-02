using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// Un servidor SMTP de verdad (Mailpit) en un contenedor aislado, para probar el transporte de correo contra algo que habla el protocolo: el cifrado, la
/// autenticación y el formato real del mensaje (spec 017, D11, ADR-008). Un doble no podría cazar un fallo de la biblioteca de correo.
/// </summary>
/// <remarks>
/// Son <b>dos</b> contenedores porque un mismo puerto no puede pedir y no pedir usuario: uno con STARTTLS y usuario (el 587 habitual) y otro sin nada (desarrollo). El TLS desde el
/// primer byte (el 465) NO se prueba aquí sino con un servidor en proceso (<see cref="ScriptedSmtpServer"/>): un antivirus que inspecciona el correo (AVG, en la máquina de desarrollo)
/// intercepta ese tipo de conexión y presenta su propio certificado, con lo que el test dependería de lo que haya instalado en cada equipo. El certificado es autofirmado y se genera en cada ejecución; los tests lo comprueban por su huella, no lo aceptan a ciegas.
/// </remarks>
public sealed class MailpitFixture : IAsyncLifetime
{
    // Imagen fijada, como la de SQL Server: los tests usan siempre la misma versión (ADR-010, ADR-020).
    private const string Image = "axllent/mailpit:v1.33.0";

    // Por dirección y no por nombre: «localhost» puede resolver primero a IPv6, y el reenvío de puertos de Docker Desktop no siempre contesta por ahí.
    private const string Loopback = "127.0.0.1";

    public const string UserName = "ofiflow-test";
    public const string Password = "ofiflow-test-pass-9Qz";

    private static readonly HttpClient Http = new();

    private X509Certificate2 _certificate = null!;
    private IContainer _startTls = null!;
    private IContainer _plain = null!;

    /// <summary>El certificado (con su clave, listo para que un servidor TLS en proceso lo use) y su huella: lo único que los tests aceptan como válido.</summary>
    public X509Certificate2 ServerCertificate { get; private set; } = null!;

    /// <summary>La huella del certificado del servidor.</summary>
    public string CertificateThumbprint => _certificate.Thumbprint;

    /// <summary>El servidor con STARTTLS ofrecido y usuario obligatorio para autenticarse (la autenticación solo se acepta ya cifrada).</summary>
    public Endpoint StartTls => new(Loopback, _startTls.GetMappedPublicPort(1025), _startTls.GetMappedPublicPort(8025));

    /// <summary>Un servidor sin cifrado, sin certificado y sin usuario: el de un entorno de desarrollo.</summary>
    public Endpoint Plain => new(Loopback, _plain.GetMappedPublicPort(1025), _plain.GetMappedPublicPort(8025));

    public async Task InitializeAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());

        var now = DateTimeOffset.UtcNow;
        _certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        // Reimportado desde PFX: Schannel (Windows) no usa una clave efímera recién creada para actuar de servidor TLS.
        ServerCertificate = X509CertificateLoader.LoadPkcs12(_certificate.Export(X509ContentType.Pfx), null);
        var certPem = Encoding.ASCII.GetBytes(_certificate.ExportCertificatePem());
        var keyPem = Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem());
        var users = Encoding.ASCII.GetBytes($"{UserName}:{Password}\n");

        _startTls = Build(certPem, keyPem)
            .WithResourceMapping(users, "/auth/users")
            .WithEnvironment("MP_SMTP_AUTH_FILE", "/auth/users")
            .Build();

        _plain = new ContainerBuilder(Image)
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithEnvironment("MP_SMTP_DISABLE_RDNS", "true")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/readyz")))
            .Build();

        await Task.WhenAll(_startTls.StartAsync(), _plain.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _startTls.DisposeAsync();
        await _plain.DisposeAsync();
        ServerCertificate.Dispose();
        _certificate.Dispose();
    }

    // MP_SMTP_DISABLE_RDNS: sin esto, Mailpit hace una búsqueda DNS inversa de cada cliente antes de saludar y, dentro de Docker Desktop, tarda unos 10 s en la
    // primera conexión (y 2 s en las siguientes): suficiente para agotar el tiempo máximo del transporte y dar un fallo que no es del código.
    private static ContainerBuilder Build(byte[] certPem, byte[] keyPem) =>
        new ContainerBuilder(Image)
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithEnvironment("MP_SMTP_DISABLE_RDNS", "true")
            .WithResourceMapping(certPem, "/certs/cert.pem")
            .WithResourceMapping(keyPem, "/certs/key.pem")
            .WithEnvironment("MP_SMTP_TLS_CERT", "/certs/cert.pem")
            .WithEnvironment("MP_SMTP_TLS_KEY", "/certs/key.pem")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/readyz")));

    /// <summary>Dónde escucha un servidor de pruebas: el puerto SMTP y el de su API para leer lo recibido.</summary>
    public sealed record Endpoint(string Host, int SmtpPort, int ApiPort)
    {
        /// <summary>El correo recibido para esta dirección, o <c>null</c> si no llegó nada (se espera un instante: el servidor lo guarda en cuanto lo acepta, pero la API puede ir un poco por detrás).</summary>
        public async Task<ReceivedMessage?> FindAsync(string recipient)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var list = await Http.GetFromJsonAsync<JsonElement>($"http://{Host}:{ApiPort}/api/v1/messages");
                foreach (var summary in list.GetProperty("messages").EnumerateArray())
                {
                    var to = summary.GetProperty("To").EnumerateArray().Select(a => a.GetProperty("Address").GetString()).ToList();
                    if (to.Contains(recipient, StringComparer.OrdinalIgnoreCase))
                    {
                        var id = summary.GetProperty("ID").GetString();
                        var detail = await Http.GetFromJsonAsync<JsonElement>($"http://{Host}:{ApiPort}/api/v1/message/{id}");
                        var raw = await Http.GetStringAsync($"http://{Host}:{ApiPort}/api/v1/message/{id}/raw");
                        return new ReceivedMessage(detail, raw);
                    }
                }

                await Task.Delay(100);
            }

            return null;
        }

        /// <summary>¿Llegó algo para esta dirección? Para comprobar que NO llegó: espera un instante razonable antes de decir que no.</summary>
        public async Task<bool> ReceivedAnythingForAsync(string recipient) => await FindAsync(recipient) is not null;
    }

    /// <summary>Un correo tal como lo guardó el servidor de pruebas: ya decodificado (<see cref="Detail"/>) y en bruto (<see cref="Raw"/>).</summary>
    public sealed record ReceivedMessage(JsonElement Detail, string Raw)
    {
        public string Subject => Detail.GetProperty("Subject").GetString()!;

        public string Text => Detail.GetProperty("Text").GetString()!;

        public string FromAddress => Detail.GetProperty("From").GetProperty("Address").GetString()!;

        public string FromName => Detail.GetProperty("From").GetProperty("Name").GetString()!;

        public IReadOnlyList<string> ToAddresses => Detail.GetProperty("To").EnumerateArray().Select(a => a.GetProperty("Address").GetString()!).ToList();

        public int CcCount => Detail.GetProperty("Cc").ValueKind == JsonValueKind.Array ? Detail.GetProperty("Cc").GetArrayLength() : 0;

        public int BccCount => Detail.GetProperty("Bcc").ValueKind == JsonValueKind.Array ? Detail.GetProperty("Bcc").GetArrayLength() : 0;

        public string MessageId => Detail.GetProperty("MessageID").GetString()!;
    }
}

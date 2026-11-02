using System.Net;
using Microsoft.Extensions.Options;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>Qué transporte entrega el correo (spec 017, R4).</summary>
public enum EmailProvider
{
    /// <summary>No se envía nada. Es el valor por defecto: los tests, el desarrollo y las demos siguen funcionando sin servidor de correo (D5).</summary>
    None = 0,

    /// <summary>Se envía por SMTP (D1).</summary>
    Smtp = 1
}

/// <summary>Cómo se protege la conexión SMTP (spec 017, R5).</summary>
public enum SmtpSecurity
{
    /// <summary>Se conecta en claro y se pasa a TLS con STARTTLS (el puerto 587 habitual). Si el servidor no lo ofrece, el envío falla: no hay vuelta atrás a texto claro.</summary>
    StartTls = 0,

    /// <summary>TLS desde el primer byte (el puerto 465 habitual).</summary>
    SslOnConnect = 1,

    /// <summary>Sin cifrar. Solo se admite con un servidor de bucle local (desarrollo y tests): fuera de él, la configuración no arranca.</summary>
    None = 2
}

/// <summary>
/// La configuración del correo (spec 017, R3–R8, D8). Como <c>RefreshTokenPurge</c> y <c>Cors</c>, se valida <b>al arrancar</b>: una configuración que no
/// vale impide arrancar en vez de fallar al primer envío. La contraseña SMTP es un secreto: vive en variables de entorno o <i>user-secrets</i>, nunca
/// en el repositorio, y ningún mensaje de esta clase la repite ni repite ningún otro valor.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>El tiempo máximo que se admite configurar para un envío: más que esto es esperar a ciegas con la petición abierta (R6).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(1);

    public const int MaxFromNameLength = 100;

    public EmailProvider Provider { get; set; } = EmailProvider.None;

    /// <summary>La dirección desde la que se envía. Obligatoria con <see cref="EmailProvider.Smtp"/>.</summary>
    public string? From { get; set; }

    /// <summary>El nombre que ve quien recibe el correo junto a la dirección.</summary>
    public string FromName { get; set; } = "OfiFlow";

    /// <summary>
    /// A dónde apuntan los enlaces de los correos (R3): <c>https://app.ejemplo.com</c>, sin ruta ni barra final. <b>Nunca</b> se deduce de la petición (cabeceras
    /// <c>Host</c> u <c>Origin</c>): lo decide el despliegue (D6). Obligatoria con <see cref="EmailProvider.Smtp"/>.
    /// </summary>
    public string? LinkBaseUrl { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>¿Hay un transporte que envíe de verdad? Los casos de uso lo comprueban antes de construir un correo (D5).</summary>
    public bool IsEnabled => Provider != EmailProvider.None;

    /// <summary>Devuelve el motivo si la configuración no vale, o <c>null</c>. Nombra el ajuste y el rango; nunca refleja un valor.</summary>
    public string? Validate()
    {
        // El enlace se valida siempre que exista: un valor mal escrito que hoy no se usa fallaría el día que se active el correo.
        if (!string.IsNullOrWhiteSpace(LinkBaseUrl) && !IsValidLinkBaseUrl(LinkBaseUrl))
        {
            return $"{SectionName}:LinkBaseUrl debe ser un origen exacto (esquema y servidor, y puerto si no es el estándar), sin ruta, sin barra final, sin parámetros ni usuario, y en https salvo en localhost.";
        }

        if (Provider == EmailProvider.None)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(LinkBaseUrl))
        {
            return $"Falta {SectionName}:LinkBaseUrl: con {SectionName}:Provider = Smtp hace falta saber a dónde apuntan los enlaces de los correos.";
        }

        if (string.IsNullOrWhiteSpace(From) || !IsPlainAddress(From))
        {
            return $"{SectionName}:From debe ser una dirección de correo (sin nombre ni espacios), por ejemplo no-reply@ejemplo.com.";
        }

        if (!IsValidFromName(FromName))
        {
            return $"{SectionName}:FromName no puede estar vacío, ni pasar de {MaxFromNameLength} caracteres, ni contener caracteres de control.";
        }

        return Smtp.Validate();
    }

    /// <summary>
    /// Una dirección simple: <c>algo@dominio.tld</c>, sin espacios, sin caracteres de control (un salto de línea inyectaría cabeceras) y sin nombre con
    /// ángulos. La regla es a propósito algo más estricta que la de <c>Email</c> porque aquí la escribe quien despliega, no quien se registra.
    /// </summary>
    internal static bool IsPlainAddress(string value)
    {
        if (value.Length > 320 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"' or ','))
        {
            return false;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == value.LastIndexOf('@') && value.IndexOf('.', at) > at + 1 && !value.EndsWith('.');
    }

    private static bool IsValidFromName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxFromNameLength && !name.Any(char.IsControl);

    private static bool IsValidLinkBaseUrl(string value)
    {
        // Sin espacios ni caracteres de control, y sin barra final: Uri la normalizaría y daría por buena una URL que el texto no cumple.
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || value.EndsWith('/'))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isLocalHttp = uri.Scheme == Uri.UriSchemeHttp && SmtpOptions.IsLoopbackHost(uri.Host);

        return (isHttps || isLocalHttp)
            && uri.AbsolutePath == "/"
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !value.Contains('?', StringComparison.Ordinal)
            && !value.Contains('#', StringComparison.Ordinal);
    }
}

/// <summary>Los ajustes del servidor SMTP (spec 017, R5, R6 y R8).</summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    /// <summary>587 (STARTTLS) por defecto; 465 para TLS implícito.</summary>
    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Usuario SMTP. Vacío si el servidor no pide autenticación (típico en desarrollo).</summary>
    public string? UserName { get; set; }

    /// <summary>Contraseña SMTP: un <b>secreto</b>. Solo por variables de entorno o <i>user-secrets</i>.</summary>
    public string? Password { get; set; }

    /// <summary>El tiempo máximo de cada envío, conexión incluida (R6). Sin reintentos.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>¿El servidor está en esta misma máquina? Es la única excepción a «el correo va cifrado» (R5).</summary>
    public bool IsLoopback => IsLoopbackHost(Host);

    internal static bool IsLoopbackHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)));

    public string? Validate()
    {
        const string section = $"{EmailOptions.SectionName}:Smtp";

        if (string.IsNullOrWhiteSpace(Host) || Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return $"Falta {section}:Host, o no es un nombre de servidor.";
        }

        if (Port is < 1 or > 65535)
        {
            return $"{section}:Port debe estar entre 1 y 65535.";
        }

        if (!Enum.IsDefined(Security))
        {
            return $"{section}:Security no es un valor conocido (StartTls, SslOnConnect o None).";
        }

        if (Security == SmtpSecurity.None && !IsLoopback)
        {
            return $"{section}:Security = None solo se admite con un servidor de esta misma máquina (localhost): el correo y las credenciales no pueden viajar sin cifrar.";
        }

        if (Timeout <= TimeSpan.Zero || Timeout > EmailOptions.MaxTimeout)
        {
            return $"{section}:Timeout debe ser mayor que cero y no pasar de {EmailOptions.MaxTimeout.TotalSeconds:0} segundos.";
        }

        if (string.IsNullOrEmpty(UserName) != string.IsNullOrEmpty(Password))
        {
            return $"{section}:UserName y {section}:Password van juntos: o se configuran los dos, o ninguno.";
        }

        return null;
    }
}

/// <summary>
/// Comprueba <see cref="EmailOptions"/> al arrancar (spec 017, R8). Es un validador propio y no un <c>.Validate(predicado)</c> para que el error diga <b>qué ajuste falla</b>: el mensaje de
/// <see cref="EmailOptions.Validate"/> nombra el ajuste y su rango, nunca el valor (podría ser la contraseña).
/// </summary>
internal sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options) =>
        options.Validate() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}

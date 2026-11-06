using Microsoft.Extensions.Options;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// Construye los enlaces de los correos (spec 017, R3, D6) <b>solo</b> con <c>Email:LinkBaseUrl</c>, un valor que decide el despliegue y que se valida al arrancar (https, sin ruta
/// ni barra final). No recibe nada de la petición HTTP: ni la cabecera <c>Host</c> ni <c>Origin</c> ni <c>Referer</c>, que controla quien la envía.
/// </summary>
internal sealed class EmailLinks(IOptions<EmailOptions> options) : IEmailLinks
{
    public string AcceptInvitation(string token) => Build("/auth/accept-invitation", token);

    public string ResetPassword(string token) => Build("/auth/reset-password", token);

    public string CompleteRegistration(string token) => Build("/auth/complete-registration", token);

    // Sin token: es la pantalla de recuperar la contraseña, que no concede nada por sí sola.
    public string ForgotPassword() => $"{BaseUrl()}/auth/forgot-password";

    private string Build(string path, string token) =>
        // El token es URL-safe (Base64 sin relleno), pero se codifica igualmente: un enlace nunca debe depender de que el contenido del token sea inofensivo.
        // En el fragmento (#), no en la consulta (?): el navegador no lo envía al servidor ni a la cabecera Referer.
        $"{BaseUrl()}{path}#token={Uri.EscapeDataString(token)}";

    private string BaseUrl()
    {
        var baseUrl = options.Value.LinkBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // No pasa con una configuración válida y el correo activo; si pasara, mejor un error que un enlace que apunta a ninguna parte.
            throw new InvalidOperationException("Falta Email:LinkBaseUrl: no se puede construir un enlace.");
        }

        return baseUrl;
    }
}

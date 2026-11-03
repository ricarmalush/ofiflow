namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Los enlaces que llevan los correos (spec 017, R3, D6). Los construye <b>solo</b> con la configuración del despliegue (<c>Email:LinkBaseUrl</c>) y nunca con datos de
/// la petición: si un enlace se construyera con la cabecera <c>Host</c> u <c>Origin</c>, quien envía la petición decidiría adónde apunta el correo que recibe la víctima.
/// </summary>
public interface IEmailLinks
{
    /// <summary>
    /// El enlace para aceptar una invitación: <c>{LinkBaseUrl}/auth/accept-invitation#token=…</c>. El token va en el <b>fragmento</b> (<c>#</c>), no en la consulta (<c>?</c>): el navegador
    /// no envía el fragmento al servidor, así que no acaba en logs de servidores ni de proxies ni en la cabecera <c>Referer</c>.
    /// </summary>
    string AcceptInvitation(string token);

    /// <summary>
    /// El enlace para restablecer la contraseña (spec 018 R5): <c>{LinkBaseUrl}/auth/reset-password#token=…</c>. Misma regla que <see cref="AcceptInvitation"/>: solo la
    /// configuración del despliegue, nunca la petición, y el token en el <b>fragmento</b>, que el navegador no envía al servidor ni al <c>Referer</c>.
    /// </summary>
    string ResetPassword(string token);
}

using System.Globalization;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy;

/// <summary>
/// El correo de una invitación (spec 017, R7, R9, R12, D7). Función pura: sin acceso a datos ni a red, para poder probarla con una tabla de casos.
/// </summary>
/// <remarks>
/// Lo que lleva, y por qué solo eso:
/// <list type="bullet">
/// <item>Un <b>asunto fijo</b>, sin ningún dato: lo que no viaja en el asunto no puede inyectar cabeceras.</item>
/// <item>Texto plano, en español. Del cuerpo salen el nombre de la empresa (de la base de datos, ya limitado en longitud), el rol (lista cerrada) y la caducidad.
/// <b>Nada que escriba quien invita</b>: así un correo de OfiFlow no sirve para escribir un texto cualquiera a un tercero.</item>
/// <item>El enlace, que ya viene construido por <c>IEmailLinks</c>.</item>
/// </list>
/// </remarks>
public static class InvitationEmail
{
    public const string Subject = "Te han invitado a unirte a una empresa en OfiFlow";

    // Los mismos nombres que enseña el frontend (ROLE_LABELS). Lista cerrada: un rol nuevo sin etiqueta lanza en vez de salir sin nombre.
    private static string RoleLabel(TenantRole role) => role switch
    {
        TenantRole.Owner => "propietario",
        TenantRole.Admin => "administrador",
        TenantRole.Manager => "responsable",
        TenantRole.Technician => "técnico",
        TenantRole.Employee => "oficina",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    public static EmailMessage Build(Email to, string tenantName, TenantRole role, DateTime expiresAtUtc, string link)
    {
        // El nombre de la empresa es texto de una persona: se deja en una sola línea y sin caracteres de control.
        var company = new string(tenantName.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        var expires = expiresAtUtc.ToString("d 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("es-ES"));

        var lines = new[]
        {
            "Hola:",
            "",
            $"Te han invitado a unirte a la empresa «{company}» en OfiFlow, con el rol de {RoleLabel(role)}.",
            "",
            "Para aceptar la invitación y elegir tu nombre y tu contraseña, abre este enlace:",
            "",
            link,
            "",
            $"La invitación caduca el {expires}. Si no esperabas este correo, ignóralo: sin abrir el enlace no pasa nada y no se crea ninguna cuenta.",
            "",
            "OfiFlow"
        };

        return new EmailMessage(to, Subject, string.Join("\r\n", lines));
    }
}

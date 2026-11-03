using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Identity;

/// <summary>
/// El correo de restablecer la contraseña (spec 018, R5). Función pura, como <c>InvitationEmail</c>: asunto <b>fijo</b>, texto plano y <b>ningún dato de la persona</b> (ni su nombre,
/// ni su empresa, ni nada que pudiera haber escrito otra): el texto es siempre el mismo y lo único que cambia es el enlace. Así un atacante que pide el restablecimiento de la cuenta
/// de otra persona no consigue que OfiFlow le envíe ningún texto suyo, y la víctima solo recibe lo de siempre.
/// </summary>
public static class PasswordResetEmail
{
    public const string Subject = "Restablece tu contraseña de OfiFlow";

    public static EmailMessage Build(Email to, string link)
    {
        var lines = new[]
        {
            "Hola:",
            "",
            "Hemos recibido una petición para restablecer la contraseña de tu cuenta de OfiFlow. Para elegir una nueva, abre este enlace:",
            "",
            link,
            "",
            "El enlace caduca en una hora y solo sirve una vez.",
            "",
            "Si no lo has pedido tú, ignora este correo: tu contraseña no cambia mientras no abras el enlace y elijas una nueva. Si te llegan varios, alguien está intentando entrar en tu cuenta: avisa a quien administre tu empresa.",
            "",
            "OfiFlow"
        };

        return new EmailMessage(to, Subject, string.Join("\r\n", lines));
    }
}

using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Identity;

/// <summary>
/// El correo con el enlace para completar el registro (spec 021, R4). Función pura, como <c>PasswordResetEmail</c>: asunto <b>fijo</b>, texto plano y <b>ningún dato de la persona</b>
/// (ni su nombre, ni el de su empresa, ni nada que pudiera haber escrito otra): el texto es siempre el mismo y lo único que cambia es el enlace. Así quien pide el registro con el email
/// de otra persona no consigue que OfiFlow le envíe ningún texto suyo, y la víctima solo recibe lo de siempre.
/// </summary>
public static class RegistrationEmail
{
    public const string Subject = "Crea tu cuenta de OfiFlow";

    public static EmailMessage Build(Email to, string link)
    {
        var lines = new[]
        {
            "Hola:",
            "",
            "Alguien ha pedido crear una cuenta de OfiFlow con esta dirección de correo. Para elegir tu contraseña y terminar, abre este enlace:",
            "",
            link,
            "",
            "El enlace caduca en 24 horas y solo sirve una vez. Hasta que lo abras no existe ninguna cuenta.",
            "",
            "Si no lo has pedido tú, ignora este correo: no se crea nada mientras no abras el enlace.",
            "",
            "OfiFlow"
        };

        return new EmailMessage(to, Subject, string.Join("\r\n", lines));
    }
}

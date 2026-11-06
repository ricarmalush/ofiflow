using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Identity;

/// <summary>
/// El aviso para quien intenta registrarse con un email que <b>ya tiene cuenta</b> (spec 021, R6). Es lo que evita que alguien que teclea bien su email y ya estaba registrado se quede esperando
/// un correo que no llega, <b>sin que la respuesta HTTP lo delate</b> (R1: es la misma exista la cuenta o no). Función pura de texto fijo: ningún dato de la persona, y su único enlace
/// <b>no lleva token</b>: es la pantalla de recuperar la contraseña, que no concede nada por sí sola.
/// </summary>
public static class AccountExistsEmail
{
    public const string Subject = "Ya tienes una cuenta de OfiFlow";

    public static EmailMessage Build(Email to, string recoverPasswordLink)
    {
        var lines = new[]
        {
            "Hola:",
            "",
            "Alguien ha intentado crear una cuenta de OfiFlow con esta dirección de correo, pero ya existe una cuenta con ella. No se ha creado nada nuevo.",
            "",
            "Si has sido tú, inicia sesión con tu cuenta de siempre. Si has olvidado la contraseña, puedes recuperarla aquí:",
            "",
            recoverPasswordLink,
            "",
            "Si no has sido tú, ignora este correo: tu cuenta no ha cambiado.",
            "",
            "OfiFlow"
        };

        return new EmailMessage(to, Subject, string.Join("\r\n", lines));
    }
}

using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Identity;

/// <summary>
/// El aviso de que la contraseña se ha cambiado (spec 018, R12, D9). Función pura, como <see cref="PasswordResetEmail"/>: asunto <b>fijo</b>, texto plano y ningún dato de la persona,
/// <b>ni siquiera un enlace</b>: no hay nada que abrir. Lo que hace sospechoso un restablecimiento que la persona no pidió es enterarse.
/// </summary>
public static class PasswordChangedEmail
{
    public const string Subject = "Tu contraseña de OfiFlow ha cambiado";

    public static EmailMessage Build(Email to)
    {
        var lines = new[]
        {
            "Hola:",
            "",
            "La contraseña de tu cuenta de OfiFlow se acaba de cambiar y se han cerrado todas tus sesiones.",
            "",
            "Si has sido tú, no tienes que hacer nada.",
            "",
            "Si no has sido tú, alguien con acceso a este buzón la ha cambiado. Restablece la contraseña otra vez desde la pantalla de acceso (\"¿Has olvidado tu contraseña?\") y avisa a quien administre tu empresa. Si tienes la verificación en dos pasos activada, sigue protegida: sin tu código nadie puede entrar.",
            "",
            "OfiFlow"
        };

        return new EmailMessage(to, Subject, string.Join("\r\n", lines));
    }
}

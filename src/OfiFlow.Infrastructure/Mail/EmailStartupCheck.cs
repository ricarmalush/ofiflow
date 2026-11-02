using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// El aviso de arranque del correo (spec 017, D5). Sin correo configurado, la invitación devuelve el token a quien invita (como antes de la spec 017): es lo correcto en desarrollo, en
/// los tests y en una demo, pero en <b>producción</b> casi seguro es un olvido. No es un error —por eso no impide arrancar—, pero tiene que verse en el registro.
/// </summary>
public static class EmailStartupCheck
{
    public static void WarnIfOffInProduction(IHostEnvironment environment, IOptions<EmailOptions> options, ILogger logger)
    {
        if (environment.IsProduction() && !options.Value.IsEnabled)
        {
            logger.LogWarning("El correo no está configurado (Email:Provider = None) en Production: las invitaciones devolverán el token a quien invita en vez de enviarlo por correo.");
        }
    }
}

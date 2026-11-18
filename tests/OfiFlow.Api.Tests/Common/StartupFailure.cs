using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// Comprueba que un ajuste inválido <b>impide arrancar la API</b> (opciones validadas al arrancar, <c>ValidateOnStart</c>), y devuelve el error de validación para mirar su mensaje.
/// </summary>
/// <remarks>
/// Por qué no es un simple <c>Assert.Throws&lt;OptionsValidationException&gt;(() =&gt; factory.CreateClient())</c>: cuando el arranque falla, el anfitrión de pruebas de ASP.NET
/// (<c>Microsoft.AspNetCore.Mvc.Testing</c>, <c>DeferredHost.StartAsync</c>) compite consigo mismo: unas veces propaga el error de validación y otras, si el contenedor ya se desechó al fallar,
/// lanza un <see cref="ObjectDisposedException"/> («Cannot access a disposed object. Object name: 'IServiceProvider'») que <b>tapa</b> el error de verdad. Es una carrera del marco, no del código de OfiFlow,
/// y salió de forma intermitente en el CI (dos veces en nueve días). Aquí se repite con un anfitrión nuevo cuando sale esa excepción; lo que <b>nunca</b> se acepta es que el anfitrión arranque,
/// ni otro error distinto, ni que la carrera se repita en todos los intentos.
/// </remarks>
public static class StartupFailure
{
    private const int MaxAttempts = 5;

    public static OptionsValidationException ExpectOptionsValidationFailure(Func<WebApplicationFactory<Program>> openFactory)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var factory = openFactory();
            try
            {
                factory.CreateClient();
            }
            catch (OptionsValidationException exception)
            {
                return exception;
            }
            catch (ObjectDisposedException)
            {
                // La carrera descrita arriba: el error de verdad quedó tapado. Se vuelve a probar con otro anfitrión.
                continue;
            }

            throw new XunitException("La API arrancó con un ajuste inválido: la validación al arrancar no se aplicó.");
        }

        throw new XunitException($"En {MaxAttempts} intentos el arranque solo devolvió ObjectDisposedException: no se pudo ver el error de validación.");
    }
}

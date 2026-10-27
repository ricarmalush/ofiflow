using System.Security.Cryptography;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// La autenticación de los operadores de la plataforma (spec 011 R7, ADR-016): un <b>esquema aparte</b>, con otra clave y otra
/// audiencia que el de las empresas, para que los dos mundos no puedan confundirse. Aquí viven los nombres que comparten el
/// registro y las rutas, la duración de la sesión y las comprobaciones de arranque de su configuración.
/// </summary>
public static class PlatformAuthentication
{
    /// <summary>El nombre del esquema de autenticación de plataforma (el de las empresas es el <c>Bearer</c> por defecto).</summary>
    public const string Scheme = "PlatformBearer";

    /// <summary>La política que exigen las rutas de <c>/api/v1/platform</c>: solo vale una sesión del esquema de plataforma.</summary>
    public const string Policy = "PlatformOperator";

    /// <summary>HMAC-SHA256: la clave son 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>Lo que dura una sesión de plataforma. Sin refresh token: pasado este tiempo hay que repetir contraseña y código (R7).</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Convierte la clave configurada en sus 32 bytes o falla con un mensaje que dice qué hacer, y comprueba que la plataforma y las
    /// empresas son <b>dos mundos distintos</b>: otra clave y otra audiencia. El mensaje cuenta qué le pasa a la configuración
    /// (falta, no es Base64, mide otra cosa, repite la de las empresas) pero <b>nunca reproduce una clave</b>.
    /// </summary>
    public static byte[] ParseKey(JwtOptions options)
    {
        const string howToSet =
            "En local: dotnet user-secrets set \"Jwt:PlatformSecret\" \"<base64 de 32 bytes>\" --project src/OfiFlow.Api " +
            "(por ejemplo con: openssl rand -base64 32; distinta de Jwt:Secret). En producción: variable de entorno Jwt__PlatformSecret " +
            "o un gestor de secretos (ADR-009 R8).";

        if (string.IsNullOrWhiteSpace(options.PlatformSecret))
        {
            throw new InvalidOperationException($"Falta 'Jwt:PlatformSecret', la clave que firma las sesiones de los operadores de la plataforma. {howToSet}");
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(options.PlatformSecret);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"'Jwt:PlatformSecret' no es Base64 válido. {howToSet}");
        }

        if (key.Length != KeyLength)
        {
            throw new InvalidOperationException(
                $"'Jwt:PlatformSecret' mide {key.Length} bytes y debe medir exactamente {KeyLength}. {howToSet}");
        }

        // Dos mundos distintos: si la clave fuera la misma, un token de empresa con la audiencia cambiada valdría en la plataforma.
        if (options.PlatformSecret == options.Secret || SameBytes(key, options.Secret))
        {
            throw new InvalidOperationException(
                "'Jwt:PlatformSecret' es igual que 'Jwt:Secret'. La plataforma y las empresas deben firmar con claves distintas, o un fallo " +
                $"en la validación dejaría que un token de empresa valiera en la plataforma. {howToSet}");
        }

        if (string.IsNullOrWhiteSpace(options.PlatformAudience) || string.Equals(options.PlatformAudience, options.Audience, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "'Jwt:PlatformAudience' debe tener valor y ser distinta de 'Jwt:Audience': un token de plataforma no puede valer como uno de empresa.");
        }

        return key;
    }

    private static bool SameBytes(byte[] platformKey, string companySecret)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(platformKey, Convert.FromBase64String(companySecret));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

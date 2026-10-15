namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// La configuración del segundo factor (ADR-014, spec 010 R3). Hoy solo guarda la clave con la que se cifran los
/// secretos TOTP; como <c>Jwt:Secret</c>, no tiene valor por defecto y la API no arranca sin ella.
/// </summary>
public sealed class MfaOptions
{
    public const string SectionName = "Mfa";

    /// <summary>AES-256: la clave son 32 bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>La clave en Base64 (32 bytes). Un secreto: va en user-secrets o en una variable de entorno, nunca en el repositorio.</summary>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>
    /// Convierte la clave configurada en sus 32 bytes o falla con un mensaje que dice qué hacer. El mensaje cuenta
    /// qué le pasa a la clave (falta, no es Base64, mide otra cosa) pero <b>nunca la reproduce</b>.
    /// </summary>
    public static byte[] ParseKey(string? base64)
    {
        const string howToSet =
            "En local: dotnet user-secrets set \"Mfa:EncryptionKey\" \"<base64 de 32 bytes>\" --project src/OfiFlow.Api " +
            "(por ejemplo con: openssl rand -base64 32). En producción: variable de entorno Mfa__EncryptionKey o un gestor de secretos (ADR-009 R8).";

        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException($"Falta 'Mfa:EncryptionKey', la clave que cifra los secretos del segundo factor. {howToSet}");
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"'Mfa:EncryptionKey' no es Base64 válido. {howToSet}");
        }

        if (key.Length != KeyLength)
        {
            throw new InvalidOperationException(
                $"'Mfa:EncryptionKey' mide {key.Length} bytes y debe medir exactamente {KeyLength} (AES-256). {howToSet}");
        }

        return key;
    }
}

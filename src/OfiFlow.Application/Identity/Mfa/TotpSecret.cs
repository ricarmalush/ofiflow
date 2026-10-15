using System.Security.Cryptography;

namespace OfiFlow.Application.Identity.Mfa;

/// <summary>
/// El secreto compartido entre el servidor y la app del móvil (spec 010 R2): cómo se genera y cómo se entrega.
/// Los bytes se cifran antes de guardarse (R3); el texto en Base32 y la URI solo se muestran una vez.
/// </summary>
public static class TotpSecret
{
    /// <summary>160 bits, el tamaño que recomienda la RFC 4226 para HMAC-SHA1.</summary>
    public const int ByteLength = 20;

    public static byte[] Generate() => RandomNumberGenerator.GetBytes(ByteLength);

    /// <summary>
    /// La URI <c>otpauth://</c> que el frontend dibuja como QR y que la app del móvil entiende. El servidor no
    /// genera el QR: así el secreto no pasa por ningún servicio de terceros. <paramref name="accountName"/> es el
    /// email, que solo se devuelve a su dueño, y se codifica para que no pueda romper la URI.
    /// </summary>
    public static string BuildUri(string issuer, string accountName, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        var escapedIssuer = Uri.EscapeDataString(issuer);

        // Los parámetros se declaran todos, aunque sean los valores por defecto: algunas apps los asumen y otras no.
        return $"otpauth://totp/{escapedIssuer}:{Uri.EscapeDataString(accountName)}" +
               $"?secret={Base32.Encode(secret)}&issuer={escapedIssuer}" +
               $"&algorithm=SHA1&digits={Totp.Digits}&period={Totp.StepSeconds}";
    }
}

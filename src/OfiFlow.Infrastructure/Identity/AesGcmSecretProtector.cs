using System.Security.Cryptography;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// AES-256-GCM: cifrado <b>autenticado</b>. Además de ocultar el contenido, detecta cualquier cambio en el texto
/// cifrado, en el nonce o en los datos asociados (ADR-014, spec 010 R3). Lo guardado es
/// <c>Base64( versión(1) | nonce(12) | etiqueta(16) | texto cifrado )</c>; el byte de versión permite rotar la clave
/// el día que haga falta sin romper lo ya guardado.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    /// <summary>La versión del formato (y de la clave). Una clave nueva sería la versión 2.</summary>
    private const byte CurrentVersion = 1;

    private const int NonceSize = 12; // 96 bits, el tamaño estándar de GCM
    private const int TagSize = 16; // 128 bits, la etiqueta más fuerte

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != MfaOptions.KeyLength)
        {
            throw new ArgumentException($"La clave debe medir {MfaOptions.KeyLength} bytes.", nameof(key));
        }

        // Copia propia: que quien la pasó no pueda cambiarla después por debajo.
        _key = (byte[])key.Clone();
    }

    public string Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        // Un nonce NUEVO y aleatorio en cada cifrado: repetirlo con la misma clave rompe GCM por completo.
        // 96 bits aleatorios no se repiten en la práctica con el número de secretos que guardamos.
        var payload = new byte[1 + NonceSize + TagSize + plaintext.Length];
        payload[0] = CurrentVersion;

        var nonce = payload.AsSpan(1, NonceSize);
        var tag = payload.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return Convert.ToBase64String(payload);
    }

    public byte[] Unprotect(string protectedValue, ReadOnlySpan<byte> associatedData)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        byte[] payload;

        try
        {
            payload = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("El secreto guardado no tiene el formato esperado.", exception);
        }

        if (payload.Length < 1 + NonceSize + TagSize)
        {
            throw new CryptographicException("El secreto guardado es demasiado corto.");
        }

        if (payload[0] != CurrentVersion)
        {
            throw new CryptographicException($"Versión de cifrado desconocida: {payload[0]}.");
        }

        var nonce = payload.AsSpan(1, NonceSize);
        var tag = payload.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);

        // Lanza CryptographicException si la etiqueta no cuadra: texto manipulado, clave o datos asociados distintos.
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return plaintext;
    }
}

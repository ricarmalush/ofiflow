using System.Security.Cryptography;
using System.Text;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El cifrado del secreto TOTP (spec 010 R3). Sin base de datos: AES-GCM es una función. Lo importante no es solo
/// que descifre lo que cifró, sino que <b>se niegue</b> a descifrar cualquier cosa manipulada o ajena.
/// </summary>
public class SecretProtectorTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] UserA = Guid.Parse("11111111-1111-1111-1111-111111111111").ToByteArray();
    private static readonly byte[] UserB = Guid.Parse("22222222-2222-2222-2222-222222222222").ToByteArray();

    private static AesGcmSecretProtector Protector(byte[]? key = null) => new(key ?? Key);

    [Fact]
    public void ProtectThenUnprotect_ReturnsTheOriginalSecret()
    {
        var stored = Protector().Protect(Secret, UserA);

        Assert.Equal(Secret, Protector().Unprotect(stored, UserA));
    }

    [Fact]
    public void TheStoredValue_DoesNotContainTheSecret_InAnyOfTheObviousEncodings()
    {
        var stored = Protector().Protect(Secret, UserA);
        var raw = Convert.FromBase64String(stored);

        Assert.DoesNotContain(Convert.ToBase64String(Secret), stored);
        Assert.DoesNotContain(Convert.ToHexString(Secret), stored, StringComparison.OrdinalIgnoreCase);
        Assert.False(raw.AsSpan().IndexOf(Secret) >= 0, "El texto guardado contiene el secreto en claro.");
    }

    [Fact]
    public void Protect_ProducesADifferentValueEveryTime_BecauseTheNonceIsNeverReused()
    {
        // Repetir el nonce con la misma clave rompe GCM (se podría recuperar el secreto). Mil cifrados del mismo
        // secreto no pueden dar ni un resultado repetido.
        var protector = Protector();
        var values = Enumerable.Range(0, 1000).Select(_ => protector.Protect(Secret, UserA)).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        // Y los 12 bytes del nonce (tras el byte de versión) tampoco se repiten.
        var nonces = values.Select(value => Convert.ToHexString(Convert.FromBase64String(value).AsSpan(1, 12))).ToList();
        Assert.Equal(nonces.Count, nonces.Distinct().Count());
    }

    [Fact]
    public void TheStoredFormat_IsVersionOneNonceTagAndCiphertext()
    {
        var raw = Convert.FromBase64String(Protector().Protect(Secret, UserA));

        Assert.Equal(1, raw[0]); // versión
        Assert.Equal(1 + 12 + 16 + Secret.Length, raw.Length); // versión + nonce + etiqueta + texto cifrado
    }

    [Fact]
    public void TheStoredValue_FitsInTheColumn()
    {
        var stored = Protector().Protect(TotpSecretOfTwentyBytes(), UserA);

        Assert.True(stored.Length <= MfaCredential.SecretCipherMaxLength, $"{stored.Length} caracteres no caben en la columna");
    }

    // ===== Se niega a descifrar lo que no es suyo =====

    [Theory]
    [InlineData(1)] // el nonce
    [InlineData(13)] // la etiqueta
    [InlineData(29)] // el texto cifrado
    public void AnyChangedByte_IsDetected(int position)
    {
        var raw = Convert.FromBase64String(Protector().Protect(Secret, UserA));
        raw[position] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(Convert.ToBase64String(raw), UserA));
    }

    [Fact]
    public void AnUnchangedValue_StillWorks_SoTheTamperingTestsAreNotPassingByAccident()
    {
        // Control positivo de la prueba anterior: sin tocar nada, descifra.
        var stored = Protector().Protect(Secret, UserA);

        Assert.Equal(Secret, Protector().Unprotect(stored, UserA));
    }

    [Fact]
    public void ADifferentKey_CannotDecrypt()
    {
        var stored = Protector(Key).Protect(Secret, UserA);

        Assert.ThrowsAny<CryptographicException>(() => Protector(OtherKey).Unprotect(stored, UserA));
    }

    [Fact]
    public void ASecretCopiedToAnotherAccount_CannotBeDecrypted_BecauseItIsBoundToTheUserId()
    {
        // Quien pudiera escribir en la base de datos podría copiar el secreto de una cuenta a la fila de otra.
        // Atado al Id de la cuenta, en la otra no se descifra.
        var storedForA = Protector().Protect(Secret, UserA);

        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(storedForA, UserB));
    }

    [Fact]
    public void AnUnknownVersion_IsRejected()
    {
        var raw = Convert.FromBase64String(Protector().Protect(Secret, UserA));
        raw[0] = 2;

        var exception = Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(Convert.ToBase64String(raw), UserA));
        Assert.Contains("Versión", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es base64 !!")]
    [InlineData("AQID")] // base64 válido pero demasiado corto
    public void ATextThatIsNotAProtectedValue_IsRejectedAsACryptographicError(string input)
    {
        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(input, UserA));
    }

    [Fact]
    public void TheConstructor_RejectsAKeyOfTheWrongLength_AndKeepsItsOwnCopy()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16]));
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[33]));

        // Cambiar el array después de crear el protector no cambia la clave que usa.
        var mutableKey = (byte[])Key.Clone();
        var protector = new AesGcmSecretProtector(mutableKey);
        var stored = protector.Protect(Secret, UserA);
        Array.Clear(mutableKey);

        Assert.Equal(Secret, protector.Unprotect(stored, UserA));
    }

    private static byte[] TotpSecretOfTwentyBytes() => RandomNumberGenerator.GetBytes(20);
}

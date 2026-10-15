using System.Security.Cryptography;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Cifra y descifra los secretos que el servidor necesita conocer en claro (el secreto TOTP): a diferencia de una
/// contraseña, no se puede guardar solo su hash, porque hace falta el secreto para calcular el código (ADR-014).
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Cifra <paramref name="plaintext"/> y devuelve texto listo para guardar. <paramref name="associatedData"/> no se
    /// cifra, pero queda atado al resultado: solo se podrá descifrar con los mismos datos. Se usa el Id de la cuenta,
    /// para que un secreto copiado a la fila de otra cuenta no sirva.
    /// </summary>
    string Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);

    /// <exception cref="CryptographicException">
    /// El texto está manipulado, es de otra clave o de otros datos asociados, o no tiene el formato esperado. Nunca devuelve basura.
    /// </exception>
    byte[] Unprotect(string protectedValue, ReadOnlySpan<byte> associatedData);
}

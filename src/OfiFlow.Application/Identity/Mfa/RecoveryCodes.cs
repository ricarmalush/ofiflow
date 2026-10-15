using System.Security.Cryptography;
using System.Text;

namespace OfiFlow.Application.Identity.Mfa;

/// <summary>
/// Los códigos de recuperación (ADR-014, spec 010 R9): lo que permite entrar si se pierde el móvil. Cada uno son
/// 16 caracteres de un alfabeto de 32 símbolos (80 bits), en grupos de cuatro: <c>XXXX-XXXX-XXXX-XXXX</c>.
/// Solo existen en claro al generarlos (se muestran una vez) y al escribirlos; en la base de datos va su hash.
/// </summary>
public static class RecoveryCodes
{
    /// <summary>El alfabeto de Crockford: cifras y letras sin I, L, O ni U, para que nadie confunda un 0 con una O al copiarlo a mano.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Length = 16;

    private const int GroupSize = 4;

    /// <summary>Genera un lote de códigos de recuperación: cada uno en claro (para mostrarlo una vez) y su hash (para guardarlo).</summary>
    public static IReadOnlyList<(string Code, string Hash)> GenerateBatch(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => Generate())
            .Select(code => (code, Hash(Normalize(code)!)))
            .ToList();

    /// <summary>Genera un código nuevo ya con guiones. 16 × 5 bits = 80 bits de un generador criptográficamente seguro.</summary>
    public static string Generate()
    {
        var builder = new StringBuilder(Length + (Length / GroupSize) - 1);

        for (var index = 0; index < Length; index++)
        {
            if (index > 0 && index % GroupSize == 0)
            {
                builder.Append('-');
            }

            // GetInt32(32) es uniforme: 32 es potencia de 2 y no hay sesgo al repartir entre 32 símbolos.
            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// La forma canónica de lo que escribe la persona: sin guiones, en mayúsculas y con las confusiones habituales
    /// resueltas como hace el alfabeto de Crockford (O→0, I y L→1). Devuelve <c>null</c> si no tiene la forma de un
    /// código (longitud o caracteres), antes de hashear ni buscar nada (ADR-009 R3).
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > Length + (Length / GroupSize) - 1)
        {
            return null;
        }

        var builder = new StringBuilder(Length);

        foreach (var character in input)
        {
            if (character == '-')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(character);
            upper = upper switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => upper
            };

            if (!Alphabet.Contains(upper, StringComparison.Ordinal))
            {
                return null;
            }

            builder.Append(upper);
        }

        return builder.Length == Length ? builder.ToString() : null;
    }

    /// <summary>
    /// SHA-256 del código normalizado, en base64. Basta un hash rápido y sin sal, a diferencia de una contraseña,
    /// porque el código es aleatorio de 80 bits: no hay diccionario que probar, y robar la base de datos no
    /// permite recorrer 2^80 candidatos. Sin sal, para poder buscarlo por el índice único.
    /// </summary>
    public static string Hash(string normalizedCode) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));
}

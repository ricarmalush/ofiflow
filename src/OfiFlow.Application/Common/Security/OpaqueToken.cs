using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace OfiFlow.Application.Common.Security;

/// <summary>
/// Un token opaco y su hash: valor aleatorio de 256 bits del que en la base de datos solo se guarda el hash. Es el
/// único sitio que sabe cómo se genera y cómo se calcula el hash, para que crear y canjear usen exactamente la misma
/// regla. Lo usan los tokens de las **invitaciones** (ADR-013, spec 009 R2) y los **desafíos del login en dos pasos**
/// (ADR-014, spec 010 R11). El valor en claro solo existe al crearlo (se entrega una vez) y al canjearlo (llega en la
/// petición); no es un JWT, así que no se puede presentar como token de acceso.
/// </summary>
public static class OpaqueToken
{
    /// <summary>256 bits de un generador criptográficamente seguro: imposible de adivinar.</summary>
    public const int ByteLength = 32;

    /// <summary>Longitud del token ya codificado (base64 apto para URL, sin relleno): 43 caracteres.</summary>
    public static readonly int ValueLength = Base64Url.GetEncodedLength(ByteLength);

    /// <summary>Crea un token nuevo y su hash. Hay que guardar el hash y entregar el valor una sola vez.</summary>
    public static (string Value, string Hash) Generate()
    {
        var value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

        return (value, Hash(value));
    }

    /// <summary>
    /// SHA-256 del token, en base64 (44 caracteres). No lleva sal: el token ya es aleatorio de 256 bits, así
    /// que no hay diccionario que atacar, y se necesita un hash determinista para poder buscarlo en el índice.
    /// </summary>
    public static string Hash(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// ¿Tiene la forma de un token real? Se comprueba antes de hashear ni buscar nada, para no procesar
    /// texto arbitrario de tamaño ilimitado (ADR-009 R3): longitud exacta y solo caracteres de base64 para URL.
    /// </summary>
    public static bool IsWellFormed(string? value) =>
        value is not null && value.Length == ValueLength && value.All(IsUrlSafeBase64Character);

    // Se comprueba el alfabeto carácter a carácter y NO con Base64Url.IsValid, que ignora los espacios en blanco:
    // un token con un espacio dentro podía darse por bueno según cuál fuera su último carácter (lo cazó un test
    // intermitente). El valor que llega se hashea tal cual, así que con un espacio nunca coincidiría con uno real,
    // pero esta función promete "solo caracteres de URL" y debe cumplirlo.
    private static bool IsUrlSafeBase64Character(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}

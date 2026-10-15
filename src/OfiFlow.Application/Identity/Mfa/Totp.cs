using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace OfiFlow.Application.Identity.Mfa;

public enum TotpOutcome
{
    /// <summary>El código es el de un paso aceptable y no se había usado: se puede dar por bueno.</summary>
    Valid,

    /// <summary>El código no corresponde a ningún paso de la ventana, o no tiene la forma de un código.</summary>
    Invalid,

    /// <summary>Es un código correcto, pero de un paso que ya se aceptó (o anterior): reutilizarlo no está permitido.</summary>
    Replayed
}

/// <param name="Outcome">Qué ha pasado con el código.</param>
/// <param name="Step">El paso al que corresponde el código; solo significa algo si el resultado es <see cref="TotpOutcome.Valid"/>.</param>
public readonly record struct TotpVerification(TotpOutcome Outcome, long Step);

/// <summary>
/// Contraseña de un solo uso basada en el tiempo (TOTP, RFC 6238), sobre HOTP (RFC 4226): HMAC-SHA1, 6 dígitos y
/// un paso de 30 segundos, que es la combinación que entienden Google Authenticator, Microsoft Authenticator,
/// Authy y los gestores de contraseñas (ADR-014, spec 010 R1). Es una función pura: el reloj entra por parámetro.
/// Se implementa sobre <see cref="HMACSHA1"/> de la BCL, sin ningún paquete: no se inventa criptografía, se aplica
/// el estándar, y se comprueba con los vectores de prueba que publican las dos RFC.
/// </summary>
public static class Totp
{
    public const int Digits = 6;

    public const int StepSeconds = 30;

    /// <summary>Pasos que se aceptan a cada lado del actual: ±30 s de desfase entre el reloj del móvil y el del servidor.</summary>
    public const int Window = 1;

    /// <summary>El número de paso (el "contador" de HOTP) al que pertenece un instante.</summary>
    public static long StepAt(DateTimeOffset moment) => moment.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>El código de un paso concreto. Con <paramref name="digits"/> distinto de 6 solo lo usan los vectores de la RFC (que son de 8).</summary>
    public static string Compute(ReadOnlySpan<byte> secret, long step, int digits = Digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 8);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HmacSha1(secret, counter, hash);

        // Truncamiento dinámico (RFC 4226 §5.3): los 4 últimos bits del hash dicen desde dónde leer 31 bits.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        var modulus = (int)Math.Pow(10, digits);

        return (binary % modulus).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// ¿Es válido este código en este instante? Acepta el paso actual y <see cref="Window"/> a cada lado, pero solo
    /// los que sean <b>posteriores</b> a <paramref name="lastAcceptedStep"/>: un mismo paso no se acepta dos veces
    /// (RFC 6238 §5.2), porque quien viera el código por encima del hombro podría reutilizarlo durante los 30 s.
    /// Quien llama debe guardar <see cref="TotpVerification.Step"/> como nuevo último paso aceptado, y hacerlo de
    /// forma atómica: si no, dos peticiones simultáneas con el mismo código pasarían las dos.
    /// </summary>
    public static TotpVerification Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long lastAcceptedStep)
    {
        if (!IsWellFormed(code))
        {
            return new TotpVerification(TotpOutcome.Invalid, 0);
        }

        var candidate = Encoding.ASCII.GetBytes(code!);
        var current = StepAt(now);

        var acceptedStep = -1L;
        var replayed = false;

        // Se calculan y comparan SIEMPRE los tres pasos, sin salir al primer acierto, para que el tiempo no
        // dependa de cuál era el bueno. La comparación es de tiempo constante.
        for (var offset = -Window; offset <= Window; offset++)
        {
            var step = current + offset;

            if (step < 0)
            {
                continue;
            }

            var expected = Encoding.ASCII.GetBytes(Compute(secret, step));

            if (!CryptographicOperations.FixedTimeEquals(expected, candidate))
            {
                continue;
            }

            if (step > lastAcceptedStep)
            {
                acceptedStep = Math.Max(acceptedStep, step);
            }
            else
            {
                replayed = true;
            }
        }

        if (acceptedStep >= 0)
        {
            return new TotpVerification(TotpOutcome.Valid, acceptedStep);
        }

        return new TotpVerification(replayed ? TotpOutcome.Replayed : TotpOutcome.Invalid, 0);
    }

    /// <summary>Exactamente 6 dígitos ASCII. Se comprueba antes de calcular nada, para no procesar texto arbitrario (ADR-009 R3).</summary>
    public static bool IsWellFormed(string? code) =>
        code is { Length: Digits } && code.All(char.IsAsciiDigit);

    // SHA-1 está roto como función hash (se pueden fabricar colisiones), pero eso no afecta a HMAC-SHA1: la
    // seguridad de un HMAC no depende de que el hash resista colisiones. Además la RFC 6238 lo exige por defecto
    // y las apps de autenticación lo asumen, así que otro algoritmo dejaría el código sin poder escanearse.
    // Es el ÚNICO sitio del proyecto donde se suprime CA5350; un test de arquitectura lo vigila.
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "HMAC-SHA1 es lo que exige la RFC 6238 y no le afectan las colisiones de SHA-1 (ADR-014).")]
    private static void HmacSha1(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination) =>
        HMACSHA1.HashData(key, data, destination);
}

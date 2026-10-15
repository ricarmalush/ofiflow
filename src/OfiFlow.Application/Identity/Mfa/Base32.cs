using System.Text;

namespace OfiFlow.Application.Identity.Mfa;

/// <summary>
/// Base32 de la RFC 4648 (alfabeto A-Z y 2-7), la codificación en la que las apps de autenticación esperan el
/// secreto TOTP. Solo codifica: el servidor guarda los bytes y únicamente los muestra en este formato una vez.
/// Sin relleno ("="): es lo que recomienda el formato de URI de las apps y lo que aceptan todas.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                result.Append(Alphabet[(buffer >> (bitsInBuffer - 5)) & 31]);
                bitsInBuffer -= 5;
            }

            // Solo hacen falta los bits que quedan pendientes; descartar el resto evita que buffer crezca sin límite.
            buffer &= (1 << bitsInBuffer) - 1;
        }

        if (bitsInBuffer > 0)
        {
            // Los bits sobrantes se completan con ceros por la derecha, como manda la RFC.
            result.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 31]);
        }

        return result.ToString();
    }
}

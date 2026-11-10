using System.Globalization;
using System.Text;

namespace OfiFlow.Infrastructure.Documents;

/// <summary>
/// Deja el texto libre (títulos, descripciones, notas, nombres, direcciones) listo para imprimirlo (spec 025 R7). El PDF imprime texto <b>tal cual</b>: aquí no hay HTML ni marcas ni enlaces que
/// interpretar, pero sí cuatro problemas de un fichero que se genera con lo que escribió una persona:
/// <list type="bullet">
/// <item>los <b>caracteres de control</b> y los de <b>formato</b> (incluidos los que invierten el sentido de lectura, como U+202E) se quitan: un nombre no puede reordenar lo que lo rodea;</item>
/// <item>un carácter que <b>la fuente no tiene</b> (un emoji, el árabe, el chino) saldría como un hueco o rompería la generación: se sustituye por «?». Se permite solo lo que la fuente incluida cubre
/// (<see cref="IsPrintable"/>, y un test lo comprueba contra la fuente real);</item>
/// <item>los <b>saltos de línea</b> se respetan donde tiene sentido (las notas, la dirección) y se convierten en espacio donde no (una descripción), con un máximo de dos seguidos;</item>
/// <item>una <b>palabra larguísima sin espacios</b> se sale de su celda y de la página: <see cref="BreakLongWords"/> la parte donde deja de caber, con un espacio, que a la vista es un salto de línea.</item>
/// </list>
/// </summary>
internal static class PdfText
{
    public const char Substitute = '?';

    /// <summary>
    /// Limpia un texto: sin controles ni caracteres de formato, con «?» donde la fuente no tiene el carácter. <paramref name="allowNewlines"/> dice si los saltos de línea se conservan (dos seguidos
    /// como máximo) o se convierten en espacio. Devuelve el texto con <c>\n</c> como único salto de línea, sin espacios ni saltos en los extremos, y <b>solo con caracteres del plano básico</b>
    /// (un carácter de fuera sale como «?», uno solo); nunca <c>null</c>.
    /// </summary>
    public static string Clean(string? text, bool allowNewlines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length);
        var newlines = 0;
        var previousWasCarriageReturn = false;

        foreach (var rune in text.EnumerateRunes())
        {
            // Un salto de línea de Windows (\r\n) es un solo salto: el \n que sigue a un \r no cuenta otra vez.
            var isLineBreak = rune.Value is '\n' or '\r' or 0x2028 or 0x2029 or 0x85;
            if (isLineBreak)
            {
                var secondOfPair = rune.Value == '\n' && previousWasCarriageReturn;
                previousWasCarriageReturn = rune.Value == '\r';
                if (secondOfPair)
                {
                    continue;
                }

                if (allowNewlines)
                {
                    newlines++;
                    if (newlines <= 2)
                    {
                        result.Append('\n');
                    }
                }
                else
                {
                    AppendSpace(result, ref newlines);
                }

                continue;
            }

            previousWasCarriageReturn = false;
            var category = Rune.GetUnicodeCategory(rune);

            // El tabulador es un espacio; el resto de controles y los de formato (U+200B, U+202E, U+FEFF...), sin asignar o de uso privado, se quitan.
            if (rune.Value == '\t' || category is UnicodeCategory.SpaceSeparator)
            {
                AppendSpace(result, ref newlines);
                continue;
            }

            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate)
            {
                continue;
            }

            newlines = 0;
            result.Append(IsPrintable(rune.Value) ? (char)rune.Value : Substitute);
        }

        return result.ToString().Trim(' ', '\n');
    }

    /// <summary>
    /// Parte las palabras que no caben en <paramref name="maxWidth"/> (en puntos) insertando un espacio donde dejan de caber. Se mide el <b>ancho real</b> de cada letra con
    /// <paramref name="charWidth"/>, no se cuentan caracteres: una «W» ocupa el doble que una «i», y un tope por número de letras o dejaría pasar la hilera de «W» o partiría palabras normales.
    /// El texto tiene que venir ya de <see cref="Clean"/> (solo caracteres del plano básico).
    /// </summary>
    public static string BreakLongWords(string text, double maxWidth, Func<char, double> charWidth)
    {
        ArgumentNullException.ThrowIfNull(charWidth);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWidth, 0d);

        var result = new StringBuilder(text.Length + 8);
        var width = 0d;

        foreach (var c in text)
        {
            if (c is ' ' or '\n')
            {
                width = 0;
                result.Append(c);
                continue;
            }

            var w = charWidth(c);
            if (width > 0 && width + w > maxWidth)
            {
                result.Append(' ');
                width = 0;
            }

            result.Append(c);
            width += w;
        }

        return result.ToString();
    }

    /// <summary>
    /// Lo que la fuente incluida (Liberation Sans) sabe dibujar y nos interesa: ASCII imprimible, Latinoamérica y Europa occidental (Latin-1 y Latin extendido A, con las tildes, la eñe, «¿» y «¡», y
    /// el espacio no separable que usan los importes), el euro y la puntuación tipográfica habitual (guiones, comillas, puntos suspensivos, viñeta). Un test comprueba que <b>cada</b> carácter de aquí
    /// está en la fuente regular y en la negrita.
    /// </summary>
    public static bool IsPrintable(int codePoint) =>
        codePoint is >= 0x20 and <= 0x7E
        or >= 0xA0 and <= 0xFF and not 0xAD
        or >= 0x100 and <= 0x17F
        or 0x20AC
        or 0x2013 or 0x2014
        or 0x2018 or 0x2019 or 0x201A or 0x201C or 0x201D or 0x201E
        or 0x2022 or 0x2026 or 0x2030;

    private static void AppendSpace(StringBuilder result, ref int newlines)
    {
        // Sin espacios repetidos ni al principio de la línea: un hueco grande no es contenido.
        if (result.Length != 0 && result[^1] is not (' ' or '\n'))
        {
            result.Append(' ');
            newlines = 0;
        }
    }
}

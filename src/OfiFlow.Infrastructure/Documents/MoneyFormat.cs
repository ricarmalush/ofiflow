using System.Globalization;
using System.Text;

namespace OfiFlow.Infrastructure.Documents;

/// <summary>
/// El importe en el formato español, «1.234,56 €», <b>desde céntimos enteros y solo con aritmética entera</b> (spec 025 R5 y R6): ningún <c>double</c>, <c>float</c> ni <c>decimal</c> toca el dinero, y no
/// depende de la cultura del servidor (en un contenedor sin datos de cultura, <c>es-ES</c> ni siquiera existe). El espacio antes del € es <b>no separable</b>, para que el símbolo nunca quede solo en
/// otra línea.
/// </summary>
internal static class MoneyFormat
{
    private const char NonBreakingSpace = (char)0xA0;

    public static string Format(long cents)
    {
        // El valor absoluto sin pasar por Math.Abs: con long.MinValue desbordaría (nunca llega, pero la función no debe depender de eso).
        var absolute = cents < 0 ? unchecked((ulong)(-(cents + 1)) + 1UL) : (ulong)cents;
        var euros = (absolute / 100).ToString(CultureInfo.InvariantCulture);
        var remainder = (int)(absolute % 100);

        var text = new StringBuilder(euros.Length + 8);
        if (cents < 0)
        {
            text.Append('-');
        }

        for (var i = 0; i < euros.Length; i++)
        {
            // Un punto cada tres cifras, contando desde la derecha.
            if (i > 0 && (euros.Length - i) % 3 == 0)
            {
                text.Append('.');
            }

            text.Append(euros[i]);
        }

        text.Append(',');
        text.Append(remainder.ToString("D2", CultureInfo.InvariantCulture));
        text.Append(NonBreakingSpace);
        text.Append('€');

        return text.ToString();
    }
}

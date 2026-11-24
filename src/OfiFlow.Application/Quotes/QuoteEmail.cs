using System.Globalization;
using System.Text;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Quotes;

/// <summary>
/// El correo de un presupuesto (spec 026, R11, R13, R14, D9, D10). Función pura: sin acceso a datos ni a red, para poder probarla con una tabla de casos.
/// </summary>
/// <remarks>
/// Lo que lleva, y por qué solo eso (la regla cerrada de la ADR-020, «nada que escriba la persona entra en el correo», se mantiene):
/// <list type="bullet">
/// <item>Un <b>asunto fijo con solo el número</b> («Presupuesto P-0012»): lo que no viaja en el asunto no puede inyectar cabeceras.</item>
/// <item>Texto plano, en español. Del cuerpo salen el nombre fiscal de la empresa (de la base de datos, <b>en una sola línea</b> y sin caracteres de control ni de formato), el número, el total, la
/// validez (con formato fijo, sin cultura del servidor) y poco más. <b>Ni el título, ni las notas, ni el nombre del cliente</b>: esos textos van dentro del PDF, que es un documento.</item>
/// <item>La dirección de respuesta es el email de la <b>empresa</b> si lo tiene: quien responde escribe a la empresa, no a OfiFlow (D10). El nombre del remitente no es el de la empresa
/// (lo decide la configuración): cualquiera podría llamarse «Banco X».</item>
/// <item>El PDF como adjunto, con el nombre que sale solo del número (spec 025 D11).</item>
/// </list>
/// </remarks>
public static class QuoteEmail
{
    private const string Fallback = "La empresa";

    public static string Subject(int quoteNumber) => "Presupuesto " + QuoteLabel.Format(quoteNumber);

    /// <param name="to">El destinatario, ya validado.</param>
    /// <param name="quoteNumber">El número correlativo del presupuesto.</param>
    /// <param name="companyLegalName">El nombre fiscal de la empresa emisora (texto de una persona: se deja en una sola línea).</param>
    /// <param name="totalCents">El total que calculó el servidor (con IVA). No se recalcula.</param>
    /// <param name="validUntil">La fecha de validez, si la tiene.</param>
    /// <param name="companyEmail">El email del perfil fiscal, si lo tiene: pasa a ser la dirección de respuesta.</param>
    /// <param name="pdf">El PDF generado, que va adjunto.</param>
    public static EmailMessage Build(Email to, int quoteNumber, string companyLegalName, long totalCents, DateOnly? validUntil, Email? companyEmail, QuotePdfFile pdf)
    {
        var company = OneLine(companyLegalName);
        var number = QuoteLabel.Format(quoteNumber);

        var lines = new List<string>
        {
            "Hola:",
            "",
            $"{company} te envía el presupuesto {number}, por un total de {MoneyFormat.Format(totalCents)} (IVA incluido)."
        };

        if (validUntil is { } until)
        {
            lines.Add($"Es válido hasta el {until.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.");
        }

        lines.Add("");
        lines.Add("Lo encontrarás en el PDF adjunto.");
        lines.Add("");
        lines.Add(companyEmail is null
            ? $"Para cualquier duda, contacta con la empresa ({company})."
            : "Para cualquier duda, responde a este correo (te contestará " + company + ").");
        lines.Add("");
        lines.Add("Este correo lo envía OfiFlow a petición de la empresa emisora. Si no esperabas este presupuesto, puedes ignorarlo.");
        lines.Add("");
        lines.Add("OfiFlow");

        return new EmailMessage(
            to,
            Subject(quoteNumber),
            string.Join("\r\n", lines),
            companyEmail,
            new EmailAttachment(pdf.FileName, "application/pdf", pdf.Content));
    }

    /// <summary>
    /// Una sola línea, sin nada que no se vea o que cambie el sentido de lectura: los espacios de cualquier clase (incluidos los separadores de línea y de párrafo, que .NET cuenta como espacio), los caracteres de control y los de formato (como U+202E, que invierte el texto) se
    /// cambian por un espacio, los espacios seguidos se juntan y se recorta. Si no queda nada, un texto genérico: el correo no sale con la empresa en blanco.
    /// </summary>
    public static string OneLine(string text)
    {
        var clean = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            var blank = char.IsWhiteSpace(c) || char.IsControl(c) || category is UnicodeCategory.Format;

            if (blank)
            {
                if (!lastWasSpace)
                {
                    clean.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            clean.Append(c);
            lastWasSpace = false;
        }

        var result = clean.ToString().TrimEnd();
        return result.Length == 0 ? Fallback : result;
    }
}

namespace OfiFlow.Application.Quotes.Pdf;

/// <summary>
/// El puerto del PDF del presupuesto (spec 025 D5, el patrón de <c>IEmailSender</c>): lo único que los casos de uso saben de cómo se maqueta. La librería que lo dibuja vive en Infrastructure
/// y Application no la conoce. Recibe un <see cref="QuotePdfModel"/> ya calculado y devuelve los bytes del documento: no toca la base de datos ni recalcula nada.
/// </summary>
public interface IQuotePdfRenderer
{
    /// <summary>Genera el PDF. Es síncrono y acotado: como mucho 100 líneas (spec 023) y textos con tope. El texto libre se imprime tal cual, sin interpretarse (R7).</summary>
    byte[] Render(QuotePdfModel model);
}

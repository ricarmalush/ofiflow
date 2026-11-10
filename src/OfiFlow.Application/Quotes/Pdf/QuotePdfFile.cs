namespace OfiFlow.Application.Quotes.Pdf;

/// <summary>
/// El PDF ya generado y el nombre con que se descarga (spec 025 R15). El nombre sale <b>solo del número</b> del presupuesto (<see cref="QuoteLabel.FileName"/>): el título y el cliente son texto libre y
/// datos personales, y no entran en una cabecera HTTP. El contenido es un documento con datos fiscales, de un cliente y precios: el <c>ToString</c> no imprime nada de eso.
/// </summary>
public sealed record QuotePdfFile(string FileName, byte[] Content)
{
    public override string ToString() => nameof(QuotePdfFile);
}

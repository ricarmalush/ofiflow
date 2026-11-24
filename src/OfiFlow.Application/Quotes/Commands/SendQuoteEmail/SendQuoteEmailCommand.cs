using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.SendQuoteEmail;

/// <summary>
/// Manda un presupuesto por correo, con su PDF adjunto, a la dirección que decide quien lo manda (spec 026 R1, D2). Pide <c>Quotes.Issue</c> (D7): es la acción que compromete un precio ante un
/// tercero, la misma que «Enviar», «Aceptar» y «Rechazar»; el permiso se comprueba antes de leer nada. <b>No cambia el estado</b> del presupuesto (R2, D1).
/// </summary>
/// <param name="Id">El presupuesto. La empresa sale del token, nunca de la petición.</param>
/// <param name="Recipient">Una sola dirección de correo (R5). Es el dato personal de un tercero: no se guarda, no se registra y no sale en ninguna respuesta (D4, D14).</param>
[RequiresPermission(Permission.QuotesIssue)]
public sealed record SendQuoteEmailCommand(Guid Id, string Recipient) : IRequest<QuoteEmailSentDto>
{
    // El ToString de un record imprime todas sus propiedades, y una es la dirección de un tercero (ADR-009 R5).
    public override string ToString() => nameof(SendQuoteEmailCommand);
}

/// <summary>
/// El resultado: solo cuándo (R1, D14). Ni el destinatario ni el nombre del fichero: una respuesta con menos datos personales no acaba en una herramienta de depuración.
/// </summary>
public sealed record QuoteEmailSentDto(DateTime EmailedAt);

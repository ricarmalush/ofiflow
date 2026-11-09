using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Quotes;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.CreateQuote;

/// <summary>
/// Crea un borrador de presupuesto (spec 023). La empresa sale del token y el número lo asigna el servidor (R6); <b>la petición nunca lleva un
/// total</b>: los importes los calcula el servidor (R5). Devuelve el Id.
/// </summary>
[RequiresPermission(Permission.QuotesWrite)]
public sealed record CreateQuoteCommand(
    Guid CustomerId,
    Guid? JobId,
    string Title,
    string? Notes,
    DateOnly? ValidUntil,
    IReadOnlyList<QuoteItemInput> Items) : IRequest<Guid>
{
    // Un record imprimiría su título, sus notas y sus líneas si alguien lo registrara o lo metiera en un mensaje de error: datos comerciales que no se escriben en ningún sitio.
    public override string ToString() => nameof(CreateQuoteCommand);
}

using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Quotes;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.UpdateQuote;

/// <summary>
/// Modifica un borrador: sustituye la cabecera y <b>todas</b> las líneas a la vez (spec 023 D6). Lleva la <see cref="Version"/> que leyó quien edita:
/// si el presupuesto cambió mientras tanto, no se pisa nada (R10).
/// </summary>
[RequiresPermission(Permission.QuotesWrite)]
public sealed record UpdateQuoteCommand(
    Guid Id,
    string Version,
    Guid? JobId,
    string Title,
    string? Notes,
    DateOnly? ValidUntil,
    IReadOnlyList<QuoteItemInput> Items) : IRequest;

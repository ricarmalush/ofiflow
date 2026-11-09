using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.DeleteQuote;

/// <summary>Elimina un <b>borrador</b> (borrado físico, spec 023 R4). Un presupuesto enviado es un compromiso y no se borra.</summary>
[RequiresPermission(Permission.QuotesWrite)]
public sealed record DeleteQuoteCommand(Guid Id) : IRequest;

using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.SendQuote;

/// <summary>Envía un borrador (Draft → Sent, spec 023 R7): exige al menos una línea y deja el instante del envío. «Enviar» solo anota: no manda nada (D1). Compromete un precio ante un cliente: exige <c>Quotes.Issue</c>, no solo escribir.</summary>
[RequiresPermission(Permission.QuotesIssue)]
public sealed record SendQuoteCommand(Guid Id) : IRequest;

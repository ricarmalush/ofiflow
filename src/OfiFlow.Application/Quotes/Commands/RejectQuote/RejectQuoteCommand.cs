using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.RejectQuote;

/// <summary>Rechaza un presupuesto enviado (Sent → Rejected, spec 023 R8). Compromete un precio ante un cliente: exige <c>Quotes.Issue</c>, no solo escribir.</summary>
[RequiresPermission(Permission.QuotesIssue)]
public sealed record RejectQuoteCommand(Guid Id) : IRequest;

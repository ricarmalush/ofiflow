using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Commands.AcceptQuote;

/// <summary>Acepta un presupuesto enviado (Sent → Accepted, spec 023 R8). Es una anotación interna: no es la firma del cliente. Compromete un precio ante un cliente: exige <c>Quotes.Issue</c>, no solo escribir.</summary>
[RequiresPermission(Permission.QuotesIssue)]
public sealed record AcceptQuoteCommand(Guid Id) : IRequest;

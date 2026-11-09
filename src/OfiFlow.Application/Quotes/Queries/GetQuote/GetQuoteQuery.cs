using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Queries.GetQuote;

/// <summary>Un presupuesto con sus líneas, su desglose de IVA y su versión (spec 023 R15).</summary>
[RequiresPermission(Permission.QuotesRead)]
public sealed record GetQuoteQuery(Guid Id) : IRequest<QuoteDto>;

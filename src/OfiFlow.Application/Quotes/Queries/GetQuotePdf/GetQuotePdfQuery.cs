using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Quotes.Pdf;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Quotes.Queries.GetQuotePdf;

/// <summary>
/// El PDF de un presupuesto (spec 025 R1). Pide <c>Quotes.Read</c> (D6): descargarlo no enseña nada que esos roles no vean ya en pantalla, y todos ellos tienen también <c>Company.Read</c> (R11).
/// Se comprueba antes de leer nada (R12). Orden: permiso, presupuesto de la empresa (<c>404</c>), perfil fiscal (<c>400</c>) y generación.
/// </summary>
[RequiresPermission(Permission.QuotesRead)]
public sealed record GetQuotePdfQuery(Guid Id) : IRequest<QuotePdfFile>;

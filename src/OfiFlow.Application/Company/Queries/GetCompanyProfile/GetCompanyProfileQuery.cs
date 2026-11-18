using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Company.Queries.GetCompanyProfile;

/// <summary>El perfil fiscal de la empresa del token, o uno vacío si todavía no lo ha guardado (spec 024 D5). No lleva ningún dato: la empresa sale del token.</summary>
[RequiresPermission(Permission.CompanyRead)]
public sealed record GetCompanyProfileQuery : IRequest<CompanyProfileDto>;

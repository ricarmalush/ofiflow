using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Company.Commands.UpsertCompanyProfile;

/// <summary>
/// Crea o sustituye el perfil fiscal de la empresa (spec 024 R1, R6): la petición lleva <b>todos</b> los campos y un opcional ausente borra el anterior. La empresa sale
/// <b>solo del token</b>: el comando no lleva ninguna.
/// </summary>
[RequiresPermission(Permission.CompanyManage)]
public sealed record UpsertCompanyProfileCommand(
    string LegalName,
    string TaxId,
    string AddressLine,
    string PostalCode,
    string City,
    string? Province,
    string? Phone,
    string? Email) : IRequest
{
    // El NIF, la dirección, el teléfono y el email son datos personales cuando la empresa es un autónomo: el `ToString` del `record` los imprimiría y acabarían en un registro (ADR-009 R5).
    public override string ToString() => nameof(UpsertCompanyProfileCommand);
}

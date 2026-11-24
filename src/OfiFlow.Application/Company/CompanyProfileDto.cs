using OfiFlow.Domain.Company;

namespace OfiFlow.Application.Company;

/// <summary>
/// El perfil fiscal tal como lo ve quien tiene <c>Company.Read</c> (spec 024). Si la empresa todavía no lo ha guardado, todos los campos son <c>null</c> e
/// <see cref="IsComplete"/> es falso: es lo normal tras registrarse, no un error (D5). Como el <c>PUT</c> exige los cinco obligatorios, «completo» equivale a «existe».
/// </summary>
public sealed record CompanyProfileDto(
    string? LegalName,
    string? TaxId,
    string? AddressLine,
    string? PostalCode,
    string? City,
    string? Province,
    string? Phone,
    string? Email,
    bool IsComplete)
{
    /// <summary>El perfil de una empresa que todavía no ha guardado nada.</summary>
    public static CompanyProfileDto Empty { get; } = new(null, null, null, null, null, null, null, null, IsComplete: false);

    /// <summary>El perfil guardado, tal como lo ven la consulta del perfil y el PDF de un presupuesto: un perfil que existe está completo (los cinco obligatorios).</summary>
    public static CompanyProfileDto From(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CompanyProfileDto(
            profile.LegalName,
            profile.TaxId.Value,
            profile.AddressLine,
            profile.PostalCode.Value,
            profile.City,
            profile.Province,
            profile.Phone?.Value,
            profile.Email?.Value,
            IsComplete: true);
    }

    // Datos personales cuando la empresa es un autónomo: el `ToString` del `record` los imprimiría (ADR-009 R5).
    public override string ToString() => nameof(CompanyProfileDto);
}

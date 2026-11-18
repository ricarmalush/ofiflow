namespace OfiFlow.Domain.Company;

/// <summary>
/// Códigos de error del perfil fiscal de la empresa (ADR-011). Son contrato público: no se cambian ni se reutilizan con otro significado.
/// El texto de cada uno está en Api/Resources/ErrorMessages.resx. Nunca llevan el valor recibido: un NIF es un dato personal (ADR-009 R5).
/// </summary>
public static class CompanyErrors
{
    /// <summary>Un error de campo: el NIF, NIE o CIF no es de un formato español conocido o su dígito de control no cuadra (R3).</summary>
    public const string TaxIdInvalid = "company.tax_id_invalid";

    /// <summary>Un error de campo: el código postal no son cinco cifras o su provincia no existe (R4).</summary>
    public const string PostalCodeInvalid = "company.postal_code_invalid";
}

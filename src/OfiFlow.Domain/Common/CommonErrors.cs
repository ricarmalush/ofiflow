namespace OfiFlow.Domain.Common;

/// <summary>
/// Códigos de error del shared kernel (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class CommonErrors
{
    public const string EmailRequired = "common.email_required";
    public const string EmailInvalid = "common.email_invalid";
}

namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// Códigos de error del contexto Tenancy (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class TenancyErrors
{
    public const string TenantNameRequired = "tenant.name_required";

    /// <summary>El TenantUser no existe en el tenant activo (o pertenece a otro: indistinguible a propósito).</summary>
    public const string TenantUserNotFound = "tenant_user.not_found";
}

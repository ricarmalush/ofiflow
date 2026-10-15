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

    /// <summary>
    /// El que actúa no puede gestionar a alguien con ese rol, o conceder ese rol (jerarquía de
    /// <see cref="ManageableRoles"/>, ADR-013). La API responde 403.
    /// </summary>
    public const string RoleNotManageable = "tenant_user.role_not_manageable";

    /// <summary>Nadie puede cambiar su propio rol ni quitarse a sí mismo (spec 009 R11). La API responde 403.</summary>
    public const string CannotManageSelf = "tenant_user.cannot_manage_self";

    /// <summary>El rol del usuario no tiene el permiso que exige la operación (ADR-012). La API responde 403.</summary>
    public const string Forbidden = "auth.forbidden";
}

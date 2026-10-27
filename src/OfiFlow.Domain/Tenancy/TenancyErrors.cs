namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// Códigos de error del contexto Tenancy (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class TenancyErrors
{
    public const string TenantNameRequired = "tenant.name_required";

    /// <summary>
    /// La empresa está suspendida (spec 011 R15): sus miembros no obtienen sesión ni pueden seguir usando una. La API
    /// responde 403 y solo a quien ya probó quién es (credenciales correctas); nadie más aprende que existe.
    /// </summary>
    public const string TenantSuspended = "tenant.suspended";

    /// <summary>Se intentó suspender una empresa que ya lo estaba (spec 011 R12). No es un éxito silencioso: no cambió nada.</summary>
    public const string TenantAlreadySuspended = "tenant.already_suspended";

    /// <summary>Se intentó reactivar una empresa que no estaba suspendida (spec 011 R12).</summary>
    public const string TenantNotSuspended = "tenant.not_suspended";

    /// <summary>
    /// La empresa que un operador quiere suspender o reactivar no existe (spec 011 R13). La API responde 404. Es el único sitio donde
    /// un <c>Id</c> de empresa llega por la petición, y solo en el panel de la plataforma.
    /// </summary>
    public const string TenantNotFound = "tenant.not_found";

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

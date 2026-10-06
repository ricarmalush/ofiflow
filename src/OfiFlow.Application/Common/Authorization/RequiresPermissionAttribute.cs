using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Authorization;

/// <summary>
/// Declara el permiso que exige un Command/Query (ADR-012 R2). Si se repite, se exigen todos.
/// Quién tiene cada permiso lo dice <see cref="RolePermissions"/>, no la operación.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequiresPermissionAttribute(Permission permission) : Attribute
{
    public Permission Permission { get; } = permission;
}

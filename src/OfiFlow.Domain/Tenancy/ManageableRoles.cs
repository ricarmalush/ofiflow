using System.Collections.Frozen;

namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// La tabla única de a qué roles puede conceder, cambiar y quitar cada rol (ADR-013, spec 009 R9).
/// Complementa a <see cref="RolePermissions"/>: el permiso <c>Users.Manage</c> dice si alguien puede
/// gestionar usuarios; esta tabla dice <b>a quién</b>. Así un Admin no puede ascender a nadie a Admin
/// ni tocar a otro Admin, y nadie puede tocar al Owner. Como en ADR-012 R2, los roles solo aparecen
/// en tablas de Domain: el resto del código pregunta, no compara roles. Va compilada e inmutable.
/// </summary>
public static class ManageableRoles
{
    private static readonly FrozenDictionary<TenantRole, FrozenSet<TenantRole>> Table = new Dictionary<TenantRole, FrozenSet<TenantRole>>
    {
        // El Owner gestiona a todos menos a sí mismo: es único por empresa y no se concede (R1, R12).
        [TenantRole.Owner] = Set(TenantRole.Admin, TenantRole.Manager, TenantRole.Technician, TenantRole.Employee),

        // El Admin gestiona el equipo, pero no a otros Admin: solo el Owner nombra o quita Admins (D2).
        [TenantRole.Admin] = Set(TenantRole.Manager, TenantRole.Technician, TenantRole.Employee),

        // Los demás roles no gestionan a nadie. Se listan con una fila vacía a propósito: un rol nuevo
        // sin fila falla un test, en vez de quedarse sin declarar.
        [TenantRole.Manager] = Set(),
        [TenantRole.Technician] = Set(),
        [TenantRole.Employee] = Set()
    }.ToFrozenDictionary();

    /// <summary>Los roles que tienen su fila en la tabla.</summary>
    public static IReadOnlyCollection<TenantRole> DeclaredRoles => Table.Keys;

    /// <summary>
    /// Los roles que puede gestionar el que actúa. Un rol que no esté en la tabla (un valor inexistente,
    /// o un rol nuevo sin declarar) no gestiona a nadie: ante la duda, se deniega.
    /// </summary>
    public static IReadOnlySet<TenantRole> For(TenantRole actor) =>
        Table.TryGetValue(actor, out var roles) ? roles : FrozenSet<TenantRole>.Empty;

    /// <summary>¿Puede el que actúa conceder, cambiar o quitar a alguien con el rol <paramref name="target"/>?</summary>
    public static bool CanManage(TenantRole actor, TenantRole target) => For(actor).Contains(target);

    /// <summary>
    /// ¿Puede el que actúa cambiar a alguien de <paramref name="currentRole"/> a <paramref name="newRole"/>?
    /// Hacen falta las dos cosas (R10): que pueda gestionar el rol actual y el nuevo. Así un Admin no puede
    /// ascender a un Manager a Admin, ni degradar a un Admin.
    /// </summary>
    public static bool CanChange(TenantRole actor, TenantRole currentRole, TenantRole newRole) =>
        CanManage(actor, currentRole) && CanManage(actor, newRole);

    /// <summary>
    /// ¿Hay algún rol capaz de conceder este rol? Es falso para el Owner y para un valor que no es un rol:
    /// una invitación o un cambio con ese rol no puede existir (R1).
    /// </summary>
    public static bool IsGrantable(TenantRole role) => Table.Values.Any(roles => roles.Contains(role));

    private static FrozenSet<TenantRole> Set(params TenantRole[] roles) => roles.ToFrozenSet();
}

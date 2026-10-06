using System.Collections.Frozen;

namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// La tabla única de qué puede hacer cada rol (ADR-012 R1, spec 008). Es el único sitio donde
/// aparecen los roles junto a los permisos: el resto del sistema decide por <see cref="Permission"/>,
/// nunca por rol. Va compilada e inmutable; cambiarla exige un despliegue.
/// </summary>
public static class RolePermissions
{
    private static readonly FrozenDictionary<TenantRole, FrozenSet<Permission>> Table = new Dictionary<TenantRole, FrozenSet<Permission>>
    {
        // Owner tiene todos los permisos por esta tabla, no por una excepción en el código (spec 008 R3).
        // Se listan uno a uno a propósito: un permiso nuevo no se concede a nadie, ni al Owner, hasta que se decide.
        [TenantRole.Owner] = Set(
            Permission.CustomersRead, Permission.CustomersWrite, Permission.CustomersDelete,
            Permission.JobsRead, Permission.JobsWrite, Permission.JobsAssign, Permission.JobsCancel, Permission.JobsExecute),

        // Hoy idéntico a Owner; se diferenciarán cuando existan funciones solo del dueño (facturación, usuarios).
        [TenantRole.Admin] = Set(
            Permission.CustomersRead, Permission.CustomersWrite, Permission.CustomersDelete,
            Permission.JobsRead, Permission.JobsWrite, Permission.JobsAssign, Permission.JobsCancel, Permission.JobsExecute),

        // Gestiona el trabajo diario, pero no borra clientes (borrado físico e irreversible).
        [TenantRole.Manager] = Set(
            Permission.CustomersRead, Permission.CustomersWrite,
            Permission.JobsRead, Permission.JobsWrite, Permission.JobsAssign, Permission.JobsCancel, Permission.JobsExecute),

        // Personal de oficina: da de alta y modifica clientes y trabajos, pero no asigna, cancela ni ejecuta.
        [TenantRole.Employee] = Set(
            Permission.CustomersRead, Permission.CustomersWrite,
            Permission.JobsRead, Permission.JobsWrite),

        // Ve clientes y trabajos y los ejecuta; no crea ni modifica. Limitación conocida (ADR-012): puede
        // ejecutar cualquier trabajo de su empresa, no solo los asignados a él.
        [TenantRole.Technician] = Set(
            Permission.CustomersRead,
            Permission.JobsRead, Permission.JobsExecute)
    }.ToFrozenDictionary();

    /// <summary>Los roles que tienen su fila en la tabla.</summary>
    public static IReadOnlyCollection<TenantRole> DeclaredRoles => Table.Keys;

    /// <summary>
    /// Los permisos de un rol. Un rol que no esté en la tabla (un valor inexistente, o un rol nuevo
    /// sin declarar) no tiene ninguno: ante la duda, se deniega.
    /// </summary>
    public static IReadOnlySet<Permission> For(TenantRole role) =>
        Table.TryGetValue(role, out var permissions) ? permissions : FrozenSet<Permission>.Empty;

    public static bool Has(TenantRole role, Permission permission) => For(role).Contains(permission);

    private static FrozenSet<Permission> Set(params Permission[] permissions) => permissions.ToFrozenSet();
}

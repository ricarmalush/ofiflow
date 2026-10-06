namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// Lo que se puede hacer en el sistema, con el formato Entidad.Acción del documento maestro
/// (sección 16). Cada Command/Query de Application exige uno; quién lo tiene lo dice una única
/// tabla, <see cref="RolePermissions"/> (ADR-012). Añadir un permiso nuevo exige decidir qué
/// roles lo reciben: hasta entonces, ninguno lo tiene.
/// </summary>
public enum Permission
{
    /// <summary>Customers.Read: ver un cliente, listar clientes.</summary>
    CustomersRead,

    /// <summary>Customers.Write: crear y modificar clientes.</summary>
    CustomersWrite,

    /// <summary>Customers.Delete: eliminar clientes (borrado físico, irreversible: ADR-006).</summary>
    CustomersDelete,

    /// <summary>Jobs.Read: ver un trabajo, listar trabajos.</summary>
    JobsRead,

    /// <summary>Jobs.Write: crear y modificar trabajos.</summary>
    JobsWrite,

    /// <summary>Jobs.Assign: asignar un trabajo a un TenantUser.</summary>
    JobsAssign,

    /// <summary>Jobs.Cancel: cancelar un trabajo.</summary>
    JobsCancel,

    /// <summary>Jobs.Execute: iniciar y completar un trabajo.</summary>
    JobsExecute
}

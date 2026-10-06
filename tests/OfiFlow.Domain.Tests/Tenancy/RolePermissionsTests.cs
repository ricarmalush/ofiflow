using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

/// <summary>
/// La matriz de permisos de la spec 008 como especificación ejecutable: cada celda de la tabla
/// aprobada se comprueba contra <see cref="RolePermissions"/>. Si alguien cambia un permiso en el
/// código sin cambiar la spec (o al revés), falla un test con el rol y el permiso concretos.
/// </summary>
public class RolePermissionsTests
{
    // Copia literal de la matriz aprobada en la spec 008, escrita aparte de la tabla de producción
    // a propósito: dos copias independientes que tienen que coincidir. X = permitido, - = denegado.
    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    private static readonly string[] SpecMatrix =
    [
        //                  Owner Admin Manager Employee Technician
        "CustomersRead      X     X     X       X        X",
        "CustomersWrite     X     X     X       X        -",
        "CustomersDelete    X     X     -       -        -",
        "JobsRead           X     X     X       X        X",
        "JobsWrite          X     X     X       X        -",
        "JobsAssign         X     X     X       -        -",
        "JobsCancel         X     X     X       -        -",
        "JobsExecute        X     X     X       -        X"
    ];

    public static TheoryData<TenantRole, Permission, bool> Cells()
    {
        var data = new TheoryData<TenantRole, Permission, bool>();

        foreach (var row in SpecMatrix)
        {
            var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var permission = Enum.Parse<Permission>(parts[0]);

            for (var column = 0; column < Columns.Length; column++)
            {
                data.Add(Columns[column], permission, parts[column + 1] == "X");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void EveryCell_MatchesTheApprovedMatrix(TenantRole role, Permission permission, bool expected)
    {
        Assert.Equal(expected, RolePermissions.Has(role, permission));
    }

    [Fact]
    public void TheApprovedMatrix_CoversEveryPermissionAndEveryRole()
    {
        // Sin esto, añadir un permiso o un rol nuevo dejaría la matriz de arriba incompleta y los
        // tests pasarían sin comprobar nada de lo nuevo.
        var permissionsInMatrix = SpecMatrix.Select(row => Enum.Parse<Permission>(row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));

        Assert.Equal(Enum.GetValues<Permission>().Order(), permissionsInMatrix.Order());
        Assert.Equal(Enum.GetValues<TenantRole>().Order(), Columns.Order());
    }

    [Fact]
    public void EveryRole_HasARowInTheTable()
    {
        var missing = Enum.GetValues<TenantRole>().Except(RolePermissions.DeclaredRoles).ToList();

        Assert.True(missing.Count == 0,
            $"Estos roles no tienen fila en RolePermissions y no podrían hacer nada: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Owner_HasEveryPermission()
    {
        var missing = Enum.GetValues<Permission>().Where(p => !RolePermissions.Has(TenantRole.Owner, p)).ToList();

        Assert.True(missing.Count == 0,
            $"Owner debe tener todos los permisos (spec 008 R3). Le faltan: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryPermission_IsGrantedToAtLeastOneRole()
    {
        var orphans = Enum.GetValues<Permission>()
            .Where(p => !Enum.GetValues<TenantRole>().Any(role => RolePermissions.Has(role, p)))
            .ToList();

        Assert.True(orphans.Count == 0,
            $"Estos permisos no los tiene ningún rol, así que nadie podría ejecutar la operación: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void ARoleOutsideTheEnum_HasNoPermissions()
    {
        // Fail-closed: un valor que no es un rol (un token manipulado, un rol nuevo sin declarar) no puede nada.
        var unknown = (TenantRole)999;

        Assert.Empty(RolePermissions.For(unknown));
        Assert.All(Enum.GetValues<Permission>(), permission => Assert.False(RolePermissions.Has(unknown, permission)));
    }

    [Fact]
    public void ThePermissionSets_CannotBeModifiedFromOutside()
    {
        var set = (ISet<Permission>)RolePermissions.For(TenantRole.Technician);

        Assert.Throws<NotSupportedException>(() => set.Add(Permission.CustomersDelete));
        Assert.False(RolePermissions.Has(TenantRole.Technician, Permission.CustomersDelete));
    }
}

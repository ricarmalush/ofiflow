using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

/// <summary>
/// La jerarquía de la spec 009 (R9) como especificación ejecutable: cada celda de la tabla aprobada se
/// comprueba contra <see cref="ManageableRoles"/>. Si alguien cambia a quién puede gestionar un rol sin
/// cambiar la spec (o al revés), falla un test con el rol que actúa y el rol objetivo concretos.
/// </summary>
public class ManageableRolesTests
{
    // Copia literal de la tabla aprobada en la spec 009, escrita aparte de la de producción a propósito:
    // dos copias independientes que tienen que coincidir. Filas = el que actúa; columnas = el rol objetivo.
    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Technician, TenantRole.Employee];

    private static readonly string[] SpecMatrix =
    [
        //            Owner Admin Manager Technician Employee
        "Owner        -     X     X       X          X",
        "Admin        -     -     X       X          X",
        "Manager      -     -     -       -          -",
        "Technician   -     -     -       -          -",
        "Employee     -     -     -       -          -"
    ];

    public static TheoryData<TenantRole, TenantRole, bool> Cells()
    {
        var data = new TheoryData<TenantRole, TenantRole, bool>();

        foreach (var row in SpecMatrix)
        {
            var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var actor = Enum.Parse<TenantRole>(parts[0]);

            for (var column = 0; column < Columns.Length; column++)
            {
                data.Add(actor, Columns[column], parts[column + 1] == "X");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void EveryCell_MatchesTheApprovedTable(TenantRole actor, TenantRole target, bool expected)
    {
        Assert.Equal(expected, ManageableRoles.CanManage(actor, target));
    }

    [Fact]
    public void TheApprovedTable_CoversEveryRoleAsActorAndAsTarget()
    {
        // Sin esto, añadir un rol nuevo dejaría la tabla de arriba incompleta y los tests pasarían sin
        // comprobar nada de lo nuevo.
        var actors = SpecMatrix.Select(row => Enum.Parse<TenantRole>(row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));

        Assert.Equal(Enum.GetValues<TenantRole>().Order(), actors.Order());
        Assert.Equal(Enum.GetValues<TenantRole>().Order(), Columns.Order());
    }

    [Fact]
    public void EveryRole_HasARowInTheTable()
    {
        var missing = Enum.GetValues<TenantRole>().Except(ManageableRoles.DeclaredRoles).ToList();

        Assert.True(missing.Count == 0,
            $"Estos roles no tienen fila en ManageableRoles: {string.Join(", ", missing)}");
    }

    [Fact]
    public void NobodyCanManageTheOwner()
    {
        // R12: el Owner es único por empresa y no se concede, no se cambia y no se quita.
        var offenders = Enum.GetValues<TenantRole>().Where(actor => ManageableRoles.CanManage(actor, TenantRole.Owner)).ToList();

        Assert.True(offenders.Count == 0,
            $"Nadie debe poder gestionar al Owner (spec 009 R12). Pueden: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NobodyCanManageThemselves_ByRoleTable()
    {
        // Ningún rol se gestiona a sí mismo en la tabla (el Admin no gestiona Admin, el Owner no gestiona Owner).
        // La regla de "no tocar tu propio TenantUser" (R11) se aplica además en Application, por Id.
        var offenders = Enum.GetValues<TenantRole>().Where(role => ManageableRoles.CanManage(role, role)).ToList();

        Assert.True(offenders.Count == 0, $"Estos roles se gestionan a sí mismos: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ARoleOutsideTheEnum_ManagesNobody_AndNobodyManagesIt()
    {
        // Fail-closed: un valor que no es un rol (un token manipulado, un rol nuevo sin declarar) no puede nada.
        var unknown = (TenantRole)999;

        Assert.Empty(ManageableRoles.For(unknown));
        Assert.All(Enum.GetValues<TenantRole>(), actor => Assert.False(ManageableRoles.CanManage(actor, unknown)));
        Assert.False(ManageableRoles.IsGrantable(unknown));
    }

    [Theory]
    [InlineData(TenantRole.Admin, true)]
    [InlineData(TenantRole.Manager, true)]
    [InlineData(TenantRole.Technician, true)]
    [InlineData(TenantRole.Employee, true)]
    [InlineData(TenantRole.Owner, false)]
    public void OnlyTheRolesSomeoneCanGrant_AreGrantable(TenantRole role, bool expected)
    {
        Assert.Equal(expected, ManageableRoles.IsGrantable(role));
    }

    [Theory]
    // El Owner puede todo lo que no toque a Owner.
    [InlineData(TenantRole.Owner, TenantRole.Manager, TenantRole.Admin, true)]
    [InlineData(TenantRole.Owner, TenantRole.Admin, TenantRole.Employee, true)]
    // El Admin no asciende a nadie a Admin (R10) ni toca a otro Admin.
    [InlineData(TenantRole.Admin, TenantRole.Manager, TenantRole.Admin, false)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, TenantRole.Manager, false)]
    // El Admin sí gestiona por debajo.
    [InlineData(TenantRole.Admin, TenantRole.Employee, TenantRole.Technician, true)]
    // Nadie convierte a alguien en Owner, ni cambia a un Owner.
    [InlineData(TenantRole.Owner, TenantRole.Manager, TenantRole.Owner, false)]
    [InlineData(TenantRole.Owner, TenantRole.Owner, TenantRole.Manager, false)]
    [InlineData(TenantRole.Admin, TenantRole.Manager, TenantRole.Owner, false)]
    // Los roles que no gestionan a nadie.
    [InlineData(TenantRole.Manager, TenantRole.Employee, TenantRole.Technician, false)]
    [InlineData(TenantRole.Technician, TenantRole.Employee, TenantRole.Manager, false)]
    [InlineData(TenantRole.Employee, TenantRole.Technician, TenantRole.Employee, false)]
    public void ChangingARole_NeedsBothTheCurrentAndTheNewRoleToBeManageable(
        TenantRole actor, TenantRole currentRole, TenantRole newRole, bool expected)
    {
        Assert.Equal(expected, ManageableRoles.CanChange(actor, currentRole, newRole));
    }

    [Fact]
    public void TheRoleSets_CannotBeModifiedFromOutside()
    {
        var set = (ISet<TenantRole>)ManageableRoles.For(TenantRole.Admin);

        Assert.Throws<NotSupportedException>(() => set.Add(TenantRole.Admin));
        Assert.False(ManageableRoles.CanManage(TenantRole.Admin, TenantRole.Admin));
    }
}

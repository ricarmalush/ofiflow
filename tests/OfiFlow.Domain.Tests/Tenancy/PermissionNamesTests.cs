using System.Text.RegularExpressions;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

/// <summary>
/// El nombre estable de cada permiso (spec 012 R3). Es un contrato con el frontend: si «Customers.Read» pasara a llamarse de
/// otra forma, la interfaz dejaría de reconocerlo sin que nada fallara. Por eso los textos exactos están escritos aquí a mano
/// y aparte del código, y un permiso nuevo no puede quedarse sin nombre ni con otro formato.
/// </summary>
public partial class PermissionNamesTests
{
    // Escrito a propósito aparte de PermissionNames: dos copias independientes que tienen que coincidir.
    private static readonly Dictionary<Permission, string> Expected = new()
    {
        [Permission.CustomersRead] = "Customers.Read",
        [Permission.CustomersWrite] = "Customers.Write",
        [Permission.CustomersDelete] = "Customers.Delete",
        [Permission.JobsRead] = "Jobs.Read",
        [Permission.JobsWrite] = "Jobs.Write",
        [Permission.JobsAssign] = "Jobs.Assign",
        [Permission.JobsCancel] = "Jobs.Cancel",
        [Permission.JobsExecute] = "Jobs.Execute",
        [Permission.UsersRead] = "Users.Read",
        [Permission.UsersManage] = "Users.Manage",
        [Permission.MfaManage] = "Mfa.Manage",
        [Permission.AccountRead] = "Account.Read",
        [Permission.AccountCloseSessions] = "Account.CloseSessions"
    };

    public static TheoryData<Permission, string> Names()
    {
        var data = new TheoryData<Permission, string>();
        foreach (var (permission, name) in Expected)
        {
            data.Add(permission, name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryPermission_HasItsExactStableName(Permission permission, string expected) =>
        Assert.Equal(expected, PermissionNames.Of(permission));

    [Fact]
    public void TheExpectedList_CoversEveryPermission()
    {
        // Sin esto, añadir un permiso dejaría la lista de arriba incompleta y los tests pasarían sin comprobar el nuevo.
        Assert.Equal(Enum.GetValues<Permission>().Order(), Expected.Keys.Order());
    }

    [Fact]
    public void EveryName_IsEntityDotAction_AndNoTwoPermissionsShareOne()
    {
        var names = Enum.GetValues<Permission>().Select(PermissionNames.Of).ToList();

        Assert.All(names, name => Assert.Matches(NameFormat(), name));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AValueOutsideTheEnum_HasNoName_ItIsNotInventedOrGuessed()
    {
        // Fail-closed: un permiso que no existe no puede ni nombrarse (el frontend lo trataría como uno válido).
        Assert.Throws<ArgumentOutOfRangeException>(() => PermissionNames.Of((Permission)999));
    }

    [Fact]
    public void ForARole_ReturnsExactlyItsPermissions_SortedAndByName()
    {
        var names = PermissionNames.For(TenantRole.Technician);

        Assert.Equal(["Account.CloseSessions", "Account.Read", "Customers.Read", "Jobs.Execute", "Jobs.Read", "Mfa.Manage"], names);
        Assert.Equal(names.Order(StringComparer.Ordinal), names); // orden estable: la respuesta no cambia de una petición a otra
    }

    [Fact]
    public void ForEveryRole_TheNamesMatchTheTableExactly()
    {
        foreach (var role in Enum.GetValues<TenantRole>())
        {
            var fromTable = RolePermissions.For(role).Select(PermissionNames.Of).Order(StringComparer.Ordinal).ToList();

            Assert.Equal(fromTable, PermissionNames.For(role));
        }
    }

    [Fact]
    public void ForARoleOutsideTheEnum_IsEmpty()
    {
        Assert.Empty(PermissionNames.For((TenantRole)999));
    }

    // Entidad de una palabra, un punto y una acción que puede tener varias («CloseSessions»): el punto separa entidad y acción, y nada más.
    [GeneratedRegex(@"^[A-Z][a-z]+\.[A-Z][a-z]+([A-Z][a-z]+)*$")]
    private static partial Regex NameFormat();
}

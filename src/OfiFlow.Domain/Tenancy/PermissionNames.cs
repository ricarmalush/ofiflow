using System.Text.RegularExpressions;

namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// El nombre estable de cada permiso, en el formato <c>Entidad.Acción</c> del documento maestro (sección 16): <c>Customers.Read</c>,
/// <c>Jobs.Assign</c>... Es el que usa <c>GET /account/me</c> (spec 012) y, por tanto, el frontend para decidir qué pintar. Se
/// <b>deriva del nombre del enum</b> en vez de escribir una tabla de textos a mano: un permiso nuevo no puede quedarse sin nombre ni
/// con otro formato (un test lo comprueba para todos y fija el texto exacto de cada uno).
/// </summary>
public static partial class PermissionNames
{
    /// <summary>El nombre de un permiso. Un valor que no es un permiso no tiene nombre: lanza, no lo inventa.</summary>
    public static string Of(Permission permission)
    {
        if (!Enum.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission), permission, "No es un permiso conocido.");
        }

        // «CustomersRead» → «Customers.Read»: el punto va donde empieza la segunda palabra.
        return Boundary().Replace(permission.ToString(), ".");
    }

    /// <summary>
    /// Los nombres de los permisos de un rol, <b>ordenados</b> para que la respuesta no cambie de una petición a otra. Sale de
    /// <see cref="RolePermissions"/>: no hay una segunda lista. Un rol que no está en la tabla no tiene ninguno.
    /// </summary>
    public static IReadOnlyList<string> For(TenantRole role) =>
        RolePermissions.For(role).Select(Of).Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex Boundary();
}

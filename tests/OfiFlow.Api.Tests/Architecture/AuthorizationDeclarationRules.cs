using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Reglas sobre cómo declara cada operación (Command/Query) su autorización (spec 008, ADR-012 R2 y R3).
/// Son funciones puras (reciben tipos, devuelven incumplimientos) para poder probar con operaciones
/// inventadas que detectan lo que dicen detectar. Complementan al AuthorizationBehavior: él deniega en
/// ejecución una operación sin declarar; estas reglas la detectan antes, al ejecutar los tests.
/// </summary>
internal static class AuthorizationDeclarationRules
{
    public static IReadOnlyList<string> Violations(IEnumerable<Type> operations)
    {
        var violations = new List<string>();

        foreach (var operation in operations)
        {
            var permissions = operation.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true)
                .Cast<RequiresPermissionAttribute>()
                .Select(attribute => attribute.Permission)
                .ToList();
            var anonymous = operation.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true);

            if (permissions.Count == 0 && !anonymous)
            {
                violations.Add(
                    $"{operation.Name}: no declara su autorización. Añade [RequiresPermission(Permission.X)] con el permiso que exige, " +
                    "o [AllowAnonymousRequest] si de verdad se ejecuta sin sesión. Mientras tanto, el AuthorizationBehavior la denegará siempre.");
                continue;
            }

            if (permissions.Count > 0 && anonymous)
            {
                violations.Add(
                    $"{operation.Name}: declara [AllowAnonymousRequest] y [RequiresPermission] a la vez. Es contradictorio: " +
                    "elige una. El AuthorizationBehavior la denegará siempre.");
                continue;
            }

            foreach (var permission in permissions)
            {
                var grantedToSomeRole = Enum.IsDefined(permission)
                    && Enum.GetValues<TenantRole>().Any(role => RolePermissions.Has(role, permission));

                if (!grantedToSomeRole)
                {
                    violations.Add(
                        $"{operation.Name}: exige el permiso {permission}, que no existe o que no tiene ningún rol en RolePermissions. " +
                        "Nadie podría ejecutarla, ni siquiera el Owner.");
                }
            }
        }

        return violations;
    }
}

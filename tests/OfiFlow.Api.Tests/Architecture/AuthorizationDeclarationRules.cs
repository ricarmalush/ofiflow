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
            var platform = operation.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true);
            var kinds = (permissions.Count > 0 ? 1 : 0) + (anonymous ? 1 : 0) + (platform ? 1 : 0);

            if (kinds == 0)
            {
                violations.Add(
                    $"{operation.Name}: no declara su autorización. Añade [RequiresPermission(Permission.X)] con el permiso que exige, " +
                    "[AllowAnonymousRequest] si de verdad se ejecuta sin sesión, o [RequiresPlatformOperator] si es una operación de la plataforma. " +
                    "Mientras tanto, el AuthorizationBehavior la denegará siempre.");
                continue;
            }

            if (kinds > 1)
            {
                violations.Add(
                    $"{operation.Name}: declara más de un tipo de autorización (permiso, anónima y plataforma son excluyentes). Es contradictorio: " +
                    "elige uno. El AuthorizationBehavior la denegará siempre.");
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

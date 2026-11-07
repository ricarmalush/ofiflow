using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Identity;

/// <summary>
/// A quién se le <b>obliga</b> a tener el segundo factor activo (spec 022 R1, ADR-024). Es <b>el único sitio</b> que lo decide: ampliarlo a otro rol
/// es cambiar una línea aquí, y un test recorre los cinco roles para que no se cambie sin querer. Va en Domain porque es una regla del negocio
/// («quien más puede, más protegido»), no un detalle de cómo se comprueba.
/// </summary>
/// <remarks>
/// <b>Owner</b> y <b>Admin</b>: son los dos roles que gestionan usuarios y roles y ven todos los datos (ADR-014, límite 3b). El resto sigue con el
/// factor opcional. Se decide con el rol <b>efectivo</b> (el de la base de datos), nunca con el que dijo el token.
/// </remarks>
public static class SecondFactorPolicy
{
    public static bool RequiredFor(TenantRole role) => role is TenantRole.Owner or TenantRole.Admin;
}

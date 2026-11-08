namespace OfiFlow.Application.Common.Authorization;

/// <summary>
/// Marca explícita de una operación que <b>puede hacer</b> quien tiene un rol que exige el segundo factor y todavía no lo ha activado (spec 022 R3). Es una
/// <b>lista cerrada</b> y de tamaño mínimo: ver quién es, ver el estado del factor y activarlo (pedir el secreto y confirmarlo). Cualquier otra operación con permiso
/// de empresa se deniega con <c>mfa.enrollment_required</c>.
/// </summary>
/// <remarks>
/// <b>Fail-closed por diseño (D2):</b> no existe una lista de operaciones <i>denegadas</i> que alguien pueda olvidar actualizar. Una operación nueva se deniega a esas
/// personas hasta que alguien la marque aquí, y un test de arquitectura fija la lista exacta, de modo que añadir una marca es una decisión visible y no un descuido.
/// No tiene efecto sobre las operaciones anónimas ni de plataforma (no pasan por esta comprobación).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AllowedWithoutSecondFactorAttribute : Attribute;

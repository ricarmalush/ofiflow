namespace OfiFlow.Application.Common.Authorization;

/// <summary>
/// Marca explícita de una operación que se ejecuta sin sesión (registro, login, renovar el token).
/// Es la única forma de saltarse la comprobación de permisos: una operación sin esta marca ni
/// <see cref="RequiresPermissionAttribute"/> se deniega (ADR-012 R3).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AllowAnonymousRequestAttribute : Attribute;

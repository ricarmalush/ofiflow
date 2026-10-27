namespace OfiFlow.Application.Common.Authorization;

/// <summary>
/// Declara que una operación es de la plataforma: solo la ejecuta un operador activo con una sesión de plataforma (spec 011
/// R14, ADR-016). Es el tercer tipo de declaración, junto a <see cref="RequiresPermissionAttribute"/> y
/// <see cref="AllowAnonymousRequestAttribute"/>, y es <b>excluyente</b> con los dos: los permisos son de una empresa y se
/// resuelven con el rol de una pertenencia que un operador no tiene. El AuthorizationBehavior comprueba en la base de datos,
/// en cada petición, que la cuenta sigue siendo un operador no dado de baja (R10).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequiresPlatformOperatorAttribute : Attribute;

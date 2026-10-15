using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.ResetUserMfa;

/// <summary>
/// Reinicia el segundo factor de otro miembro de la empresa (spec 010): para quien perdió el móvil <b>y</b> los códigos de
/// recuperación. Sigue la jerarquía de la spec 009 (<see cref="ManageableRoles"/>): el <c>Owner</c> reinicia a cualquiera,
/// el <c>Admin</c> a Manager, Technician y Employee, y nadie reinicia al <c>Owner</c> ni a sí mismo (para uno mismo está
/// desactivar, que exige contraseña y código). Borra su secreto y sus códigos y cierra todas sus sesiones.
/// <c>TenantUserId</c> es el de la pertenencia. Es idempotente: si el miembro no tenía MFA, no falla.
/// </summary>
[RequiresPermission(Permission.UsersManage)]
public sealed record ResetUserMfaCommand(Guid TenantUserId) : IRequest;

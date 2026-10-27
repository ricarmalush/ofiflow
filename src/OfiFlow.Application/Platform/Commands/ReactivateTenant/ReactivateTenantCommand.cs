using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Platform.Commands.ReactivateTenant;

/// <summary>
/// Reactiva una empresa suspendida (spec 011 R12): sus miembros vuelven a poder entrar y su sesión anterior, si no ha caducado, vuelve a
/// valer. Reactivar una que no estaba suspendida es un error de negocio (<c>tenant.not_suspended</c>).
/// </summary>
/// <remarks>Como <c>SuspendTenantCommand</c>, <paramref name="TenantId"/> es el objetivo de la acción (excepción nombrada de la regla sobre <c>TenantId</c>).</remarks>
[RequiresPlatformOperator]
public sealed record ReactivateTenantCommand(Guid TenantId) : IRequest;

using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Platform.Commands.SuspendTenant;

/// <summary>
/// Suspende una empresa (spec 011 R12): sus miembros dejan de poder entrar y de seguir usando una sesión, y no se borra ni se modifica
/// ningún dato suyo. Suspender una ya suspendida es un error de negocio (<c>tenant.already_suspended</c>), no un éxito silencioso.
/// </summary>
/// <remarks>
/// <paramref name="TenantId"/> es el <b>objetivo</b> de la acción, no el contexto de empresa de quien la pide (un operador no tiene
/// ninguno). Es el único caso del sistema donde un <c>Id</c> de empresa entra por la petición, y el test de arquitectura que lo prohíbe
/// en todas las demás operaciones lo declara como excepción <b>nombrada</b> de esta y de <c>ReactivateTenantCommand</c>.
/// </remarks>
[RequiresPlatformOperator]
public sealed record SuspendTenantCommand(Guid TenantId) : IRequest;

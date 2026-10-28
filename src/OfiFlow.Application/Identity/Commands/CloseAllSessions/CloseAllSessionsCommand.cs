using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.CloseAllSessions;

/// <summary>
/// Cierra todas las sesiones de la <b>propia</b> cuenta (spec 014). No tiene parámetros a propósito: la cuenta sale del token ya
/// validado, nunca de la petición, así que no hay un Id que falsificar para cerrar las sesiones de otra persona.
/// </summary>
[RequiresPermission(Permission.AccountCloseSessions)]
public sealed record CloseAllSessionsCommand : IRequest<Unit>;

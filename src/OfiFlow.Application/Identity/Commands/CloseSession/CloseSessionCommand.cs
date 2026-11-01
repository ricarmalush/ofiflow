using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.CloseSession;

/// <summary>
/// Cierra una sesión concreta de mi cuenta (spec 016 R13 a R17). El <paramref name="SessionId"/> solo elige <b>cuál</b> de MIS sesiones: la cuenta sale del
/// token. Es el mismo permiso que «cerrar todas» (D7): «cerrar sesiones» es una sola cosa, en singular o en plural.
/// </summary>
[RequiresPermission(Permission.AccountCloseSessions)]
public sealed record CloseSessionCommand(Guid SessionId) : IRequest<Unit>;

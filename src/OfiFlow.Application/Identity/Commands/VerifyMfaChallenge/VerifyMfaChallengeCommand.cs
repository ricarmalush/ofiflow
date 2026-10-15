using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.VerifyMfaChallenge;

/// <summary>
/// Segundo paso del login (spec 010 R11): canjea el desafío que dio la contraseña, con un código de la app o uno de
/// recuperación, por la sesión. Es anónimo: el desafío es la credencial del primer paso. Devuelve <c>null</c> (401) en
/// <b>todos</b> los fallos —desafío desconocido, caducado, usado o agotado; código erróneo o repetido; cuenta
/// bloqueada—, sin decir cuál fue.
/// </summary>
[AllowAnonymousRequest]
public sealed record VerifyMfaChallengeCommand(string Challenge, string Code) : IRequest<AuthResultDto?>;

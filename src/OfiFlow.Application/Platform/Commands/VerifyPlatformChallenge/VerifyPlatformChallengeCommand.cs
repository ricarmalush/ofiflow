using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Platform.Commands.VerifyPlatformChallenge;

/// <summary>
/// Segundo paso del inicio de sesión de plataforma (spec 011 R5, R8 y R9): canjea el desafío que dio la contraseña, con un
/// código de la app o de recuperación, por la sesión de plataforma. Solo canjea desafíos <b>de plataforma</b>: uno de empresa
/// se trata como uno inventado. Es el <b>único</b> sitio que emite una sesión de plataforma. Devuelve <c>null</c> (401) en
/// todos los fallos, sin decir cuál fue.
/// </summary>
[AllowAnonymousRequest]
public sealed record VerifyPlatformChallengeCommand(string Challenge, string Code) : IRequest<PlatformSessionDto?>;

using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Identity;

namespace OfiFlow.Application.Platform.Commands.PlatformLogin;

/// <summary>
/// Primer paso del inicio de sesión de un operador de la plataforma (spec 011 R5 y R6). <b>Nunca devuelve una sesión</b>: con
/// las credenciales de un operador activo devuelve un desafío que se canjea con
/// <see cref="VerifyPlatformChallenge.VerifyPlatformChallengeCommand"/>; en <b>cualquier</b> otro caso devuelve <c>null</c>
/// (401), sin decir cuál fue: email desconocido, contraseña errónea, cuenta bloqueada, cuenta que no es de operador (una
/// cuenta de empresa con su contraseña correcta no aprende nada), operador dado de baja o sin segundo factor.
/// </summary>
[AllowAnonymousRequest]
public sealed record PlatformLoginCommand(string Email, string Password) : IRequest<MfaChallengeDto?>;

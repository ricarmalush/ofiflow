using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.Login;

/// <summary>
/// Primer paso del login. Sin segundo factor devuelve la sesión; con él, un desafío que se canjea con
/// <see cref="VerifyMfaChallenge.VerifyMfaChallengeCommand"/> (spec 010 R10).
/// </summary>
[AllowAnonymousRequest]
public sealed record LoginCommand(string Email, string Password) : IRequest<LoginResult>;

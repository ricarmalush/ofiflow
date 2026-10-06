using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.RefreshToken;

[AllowAnonymousRequest]
public sealed record RefreshTokenCommand(string RefreshToken) : IRequest<AuthResultDto?>;

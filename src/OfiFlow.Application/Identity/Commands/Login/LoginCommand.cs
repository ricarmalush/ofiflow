using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.Login;

[AllowAnonymousRequest]
public sealed record LoginCommand(string Email, string Password) : IRequest<AuthResultDto?>;

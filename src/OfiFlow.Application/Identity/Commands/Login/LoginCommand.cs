using MediatR;

namespace OfiFlow.Application.Identity.Commands.Login;

public sealed record LoginCommand(string Email, string Password) : IRequest<AuthResultDto?>;

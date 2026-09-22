using MediatR;

namespace OfiFlow.Application.Identity.Commands.Register;

public sealed record RegisterCommand(string CompanyName, string UserName, string Email, string Password) : IRequest<Guid>;

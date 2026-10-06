using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.Register;

[AllowAnonymousRequest]
public sealed record RegisterCommand(string CompanyName, string UserName, string Email, string Password) : IRequest<Guid>;

using MediatR;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Identity.Commands.Logout;

public sealed class LogoutCommandHandler(ITokenService tokenService) : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Unit> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        // El resultado no sale de aquí: quien cierra con un token que no existe recibe lo mismo que quien cierra uno real (R4).
        await tokenService.RevokeRefreshTokenAsync(request.RefreshToken, cancellationToken);
        return Unit.Value;
    }
}

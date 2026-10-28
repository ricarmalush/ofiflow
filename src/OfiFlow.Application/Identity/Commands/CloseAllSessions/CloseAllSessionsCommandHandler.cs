using MediatR;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Identity.Commands.CloseAllSessions;

public sealed class CloseAllSessionsCommandHandler(ITokenService tokenService, ICurrentUser currentUser)
    : IRequestHandler<CloseAllSessionsCommand, Unit>
{
    public async Task<Unit> Handle(CloseAllSessionsCommand request, CancellationToken cancellationToken)
    {
        // El UserId sale del token (R1), no de la petición. El AuthorizationBehavior ya comprobó que existe y que sigue en la empresa.
        // El recuento no sale de aquí: la respuesta es la misma haya o no sesiones activas (R4); el evento lo deja el servicio (R6).
        await tokenService.RevokeAllRefreshTokensAsync(currentUser.UserId!.Value, cancellationToken);
        return Unit.Value;
    }
}

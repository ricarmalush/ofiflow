using MediatR;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Commands.CloseSession;

public sealed class CloseSessionCommandHandler(ITokenService tokenService, ICurrentUser currentUser) : IRequestHandler<CloseSessionCommand, Unit>
{
    public async Task<Unit> Handle(CloseSessionCommand request, CancellationToken cancellationToken)
    {
        // R13: el servicio busca por el Id de sesión Y por la cuenta del token. R15: se puede cerrar la propia sesión actual.
        var closed = await tokenService.CloseSessionAsync(
            currentUser.UserId!.Value, request.SessionId, isCurrentSession: request.SessionId == currentUser.SessionId, cancellationToken);

        // R14: no existe, es de otra cuenta o ya estaba cerrada son el MISMO 404 (mismo código, mismo cuerpo): la ruta no es un oráculo de sesiones ajenas.
        return closed ? Unit.Value : throw new NotFoundException(IdentityErrors.SessionNotFound, request.SessionId);
    }
}

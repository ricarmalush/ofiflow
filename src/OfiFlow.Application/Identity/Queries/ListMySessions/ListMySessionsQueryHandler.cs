using MediatR;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Identity.Queries.ListMySessions;

public sealed class ListMySessionsQueryHandler(ITokenService tokenService, ICurrentUser currentUser) : IRequestHandler<ListMySessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<IReadOnlyList<SessionDto>> Handle(ListMySessionsQuery request, CancellationToken cancellationToken)
    {
        // R10: siempre la cuenta del token, nunca un dato de la petición.
        var sessions = await tokenService.ListActiveSessionsAsync(currentUser.UserId!.Value, cancellationToken);
        var current = currentUser.SessionId;

        // R11: de cada sesión solo sale lo que está en SessionDto. El navegador se convierte en una etiqueta de un conjunto cerrado (DeviceLabel) y nunca
        // se copia crudo. El orden (la más reciente primero) se fija aquí y no se deja al servicio.
        return [.. sessions
            .OrderByDescending(session => session.LastActiveAt)
            .Select(session => new SessionDto(
                session.SessionId, DeviceLabel.From(session.UserAgent), session.StartedAt, session.LastActiveAt, session.IpAddress, Current: session.SessionId == current))];
    }
}

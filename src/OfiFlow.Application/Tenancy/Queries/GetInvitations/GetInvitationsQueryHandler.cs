using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Queries.GetInvitations;

public sealed class GetInvitationsQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser, TimeProvider timeProvider)
    : IRequestHandler<GetInvitationsQuery, List<InvitationDto>>
{
    public async Task<List<InvitationDto>> Handle(GetInvitationsQuery request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Solo las de roles que quien consulta puede gestionar: un Admin no ve las invitaciones de otros Admin
        // (las crea el Owner), igual que no puede revocarlas.
        var manageable = ManageableRoles.For(currentUser.Role!.Value).ToArray();

        var invitations = await dbContext.Invitations
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now && manageable.Contains(i.Role))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        // Sin el TokenHash ni nada con lo que se pueda aceptar: nunca sale de la base de datos hacia la API.
        return invitations
            .Select(i => new InvitationDto(i.Id, i.Email.Value, i.Role.ToString(), i.ExpiresAt, i.CreatedAt))
            .ToList();
    }
}

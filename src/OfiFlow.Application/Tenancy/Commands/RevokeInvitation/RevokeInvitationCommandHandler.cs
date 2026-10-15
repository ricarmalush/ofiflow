using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.RevokeInvitation;

public sealed partial class RevokeInvitationCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<RevokeInvitationCommandHandler> logger)
    : IRequestHandler<RevokeInvitationCommand>
{
    public async Task Handle(RevokeInvitationCommand request, CancellationToken cancellationToken)
    {
        // El filtro de tenant ya restringe la búsqueda: la invitación de otra empresa se reporta igual que una
        // inexistente (ADR-002, spec 009 R13).
        var invitation = await dbContext.Invitations.FirstOrDefaultAsync(i => i.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(InvitationErrors.NotFound, request.Id);

        var actorRole = currentUser.Role!.Value;

        // Misma jerarquía que al invitar: un Admin no revoca la invitación de un Admin.
        if (!ManageableRoles.CanManage(actorRole, invitation.Role))
        {
            AuthorizationLog.AccessDenied(logger, nameof(RevokeInvitationCommand), AuthorizationLog.Reasons.RoleNotManageable,
                currentUser.UserId, currentUser.TenantId, actorRole.ToString());
            throw new ForbiddenException(TenancyErrors.RoleNotManageable);
        }

        // Si ya no está pendiente (usada, revocada o caducada), Domain lanza DomainException invitation.invalid.
        invitation.Revoke(timeProvider.GetUtcNow().UtcDateTime);

        await dbContext.SaveChangesAsync(cancellationToken);

        LogInvitationRevoked(logger, invitation.Id, invitation.TenantId, currentUser.UserId);
    }

    [LoggerMessage(EventId = SecurityEventIds.InvitationRevoked, Level = LogLevel.Information,
        Message = "Invitación revocada. InvitationId {InvitationId}, TenantId {TenantId}, revocada por {UserId}")]
    private static partial void LogInvitationRevoked(ILogger logger, Guid invitationId, Guid tenantId, Guid? userId);
}

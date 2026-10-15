using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.InviteUser;

public sealed partial class InviteUserCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IIdentityService identityService,
    TimeProvider timeProvider,
    ILogger<InviteUserCommandHandler> logger)
    : IRequestHandler<InviteUserCommand, InvitationCreatedDto>
{
    public async Task<InvitationCreatedDto> Handle(InviteUserCommand request, CancellationToken cancellationToken)
    {
        // El AuthorizationBehavior ya garantizó que hay un usuario con un rol válido y el permiso Users.Manage.
        var actorRole = currentUser.Role!.Value;

        // Tener Users.Manage no basta: hay que poder gestionar ESE rol (jerarquía, spec 009 R1 y R9). Así un Admin
        // no invita a otro Admin, y nadie invita al Owner.
        if (!ManageableRoles.CanManage(actorRole, request.Role))
        {
            AuthorizationLog.AccessDenied(logger, nameof(InviteUserCommand), AuthorizationLog.Reasons.RoleNotManageable,
                currentUser.UserId, currentUser.TenantId, actorRole.ToString());
            throw new ForbiddenException(TenancyErrors.RoleNotManageable);
        }

        var email = Email.Create(request.Email);

        // R6: hoy una cuenta pertenece a una sola empresa, así que no se invita a quien ya tiene cuenta.
        if (await identityService.IsEmailRegisteredAsync(email.Value, cancellationToken))
        {
            throw new IdentityOperationException([IdentityErrors.EmailAlreadyRegistered]);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var normalizedEmail = Invitation.NormalizeEmail(email);

        // El filtro de tenant limita esta consulta a la empresa activa. Pendiente = ni aceptada, ni revocada, ni caducada.
        var pending = dbContext.Invitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);

        // R5: una pendiente por (empresa, email)...
        if (await pending.AnyAsync(i => i.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            throw new BusinessRuleException(InvitationErrors.AlreadyPending);
        }

        // ...y un máximo por empresa (límite de abuso).
        if (await pending.CountAsync(cancellationToken) >= Invitation.MaxPendingPerTenant)
        {
            throw new BusinessRuleException(InvitationErrors.LimitReached);
        }

        var (token, hash) = InvitationToken.Generate();
        var invitation = Invitation.Create(tenantContext.TenantId, email, request.Role, hash, currentUser.UserId!.Value, now);

        dbContext.Invitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogInvitationCreated(logger, invitation.Id, invitation.TenantId, currentUser.UserId, request.Role.ToString());

        return new InvitationCreatedDto(invitation.Id, token, invitation.ExpiresAt);
    }

    // Solo Ids y el rol: nunca el email ni el token (ADR-009 R5).
    [LoggerMessage(EventId = SecurityEventIds.InvitationCreated, Level = LogLevel.Information,
        Message = "Invitación creada. InvitationId {InvitationId}, TenantId {TenantId}, creada por {UserId}, rol {Role}")]
    private static partial void LogInvitationCreated(ILogger logger, Guid invitationId, Guid tenantId, Guid? userId, string role);
}

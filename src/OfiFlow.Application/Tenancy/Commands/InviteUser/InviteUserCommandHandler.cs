using OfiFlow.Application.Common.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
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
    IEmailSender emailSender,
    IEmailLinks emailLinks,
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

        var (token, hash) = OpaqueToken.Generate();
        var invitation = Invitation.Create(tenantContext.TenantId, email, request.Role, hash, currentUser.UserId!.Value, now);

        // Spec 017: con correo configurado, el enlace se ENVÍA y quien invita no llega a conocer el token. Se envía ANTES de guardar (D3, R11): si el envío falla no queda
        // nada que deshacer, y si guardar falla después de enviar, la persona recibe un enlace que no existe, que falla cerrado y es inofensivo. Y se envía DESPUÉS de todas
        // las reglas de arriba (R13): no se manda correo por una invitación que se iba a rechazar.
        var emailSent = emailSender.IsEnabled;
        if (emailSent)
        {
            await SendInvitationEmailAsync(invitation, email, request.Role, token, cancellationToken);
        }

        dbContext.Invitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogInvitationCreated(logger, invitation.Id, invitation.TenantId, currentUser.UserId, request.Role.ToString());

        // El token en claro solo se devuelve si no hubo otra vía de entrega.
        return new InvitationCreatedDto(invitation.Id, emailSent ? null : token, invitation.ExpiresAt, emailSent);
    }

    private async Task SendInvitationEmailAsync(Invitation invitation, Email email, TenantRole role, string token, CancellationToken cancellationToken)
    {
        var tenantName = await dbContext.Tenants.Where(t => t.Id == tenantContext.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        var message = InvitationEmail.Build(email, tenantName, role, invitation.ExpiresAt, emailLinks.AcceptInvitation(token));

        try
        {
            await emailSender.SendAsync(message, cancellationToken);
        }
        catch (EmailDeliveryException exception)
        {
            // Solo la categoría: ni la dirección ni el texto del servidor de correo (R1, R15). A quien invita se le dice que no salió, sin el motivo.
            LogInvitationEmailFailed(logger, tenantContext.TenantId, currentUser.UserId, exception.Category.ToString());
            throw new ExternalServiceException(InvitationErrors.EmailNotSent);
        }

        LogInvitationEmailSent(logger, invitation.Id, tenantContext.TenantId, currentUser.UserId);
    }

    // Solo Ids y el rol: nunca el email ni el token (ADR-009 R5).
    [LoggerMessage(EventId = SecurityEventIds.InvitationCreated, Level = LogLevel.Information,
        Message = "Invitación creada. InvitationId {InvitationId}, TenantId {TenantId}, creada por {UserId}, rol {Role}")]
    private static partial void LogInvitationCreated(ILogger logger, Guid invitationId, Guid tenantId, Guid? userId, string role);

    // Ids únicamente: ni la dirección, ni el token, ni el enlace (spec 017 R15).
    [LoggerMessage(EventId = SecurityEventIds.InvitationEmailSent, Level = LogLevel.Information,
        Message = "Correo de invitación entregado al servidor de correo. InvitationId {InvitationId}, TenantId {TenantId}, enviada por {UserId}")]
    private static partial void LogInvitationEmailSent(ILogger logger, Guid invitationId, Guid tenantId, Guid? userId);

    // Solo la categoría del fallo: el texto del servidor de correo suele repetir la dirección.
    [LoggerMessage(EventId = SecurityEventIds.InvitationEmailFailed, Level = LogLevel.Warning,
        Message = "No se pudo enviar el correo de una invitación, que no se ha creado. TenantId {TenantId}, solicitada por {UserId}, categoría {Category}")]
    private static partial void LogInvitationEmailFailed(ILogger logger, Guid tenantId, Guid? userId, string category);
}

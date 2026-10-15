using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.ChangeUserRole;

public sealed partial class ChangeUserRoleCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    ILogger<ChangeUserRoleCommandHandler> logger)
    : IRequestHandler<ChangeUserRoleCommand>
{
    public async Task Handle(ChangeUserRoleCommand request, CancellationToken cancellationToken)
    {
        // El filtro de empresa restringe la búsqueda: el miembro de otra empresa se reporta igual que uno inexistente
        // (ADR-002, spec 009 R13).
        var target = await dbContext.TenantUsers.FirstOrDefaultAsync(tu => tu.Id == request.TenantUserId, cancellationToken)
            ?? throw new NotFoundException(TenancyErrors.TenantUserNotFound, request.TenantUserId);

        var actorRole = currentUser.Role!.Value;

        // R11: nadie cambia su propio rol (evita ascenderse a sí mismo).
        if (target.UserId == currentUser.UserId)
        {
            Deny(AuthorizationLog.Reasons.CannotManageSelf, actorRole);
            throw new ForbiddenException(TenancyErrors.CannotManageSelf);
        }

        // R9, R10 y R12: hay que poder gestionar el rol ACTUAL del miembro y también el NUEVO. Así un Admin no asciende
        // a nadie a Admin, ni toca a otro Admin, y nadie toca al Owner.
        if (!ManageableRoles.CanChange(actorRole, target.Role, request.NewRole))
        {
            Deny(AuthorizationLog.Reasons.RoleNotManageable, actorRole);
            throw new ForbiddenException(TenancyErrors.RoleNotManageable);
        }

        // Mismo rol: no hay nada que cambiar ni que registrar.
        if (target.Role == request.NewRole)
        {
            return;
        }

        var previousRole = target.Role;
        target.ChangeRole(request.NewRole);

        await dbContext.SaveChangesAsync(cancellationToken);

        LogRoleChanged(logger, target.Id, target.TenantId, currentUser.UserId, previousRole.ToString(), request.NewRole.ToString());
    }

    private void Deny(string reason, TenantRole actorRole) =>
        AuthorizationLog.AccessDenied(logger, nameof(ChangeUserRoleCommand), reason, currentUser.UserId, currentUser.TenantId, actorRole.ToString());

    // Solo Ids y roles: nunca el nombre ni el email del miembro (ADR-009 R5).
    [LoggerMessage(EventId = SecurityEventIds.UserRoleChanged, Level = LogLevel.Warning,
        Message = "Rol cambiado. TenantUserId {TenantUserId}, TenantId {TenantId}, cambiado por {UserId}, de {PreviousRole} a {NewRole}")]
    private static partial void LogRoleChanged(ILogger logger, Guid tenantUserId, Guid tenantId, Guid? userId, string previousRole, string newRole);
}

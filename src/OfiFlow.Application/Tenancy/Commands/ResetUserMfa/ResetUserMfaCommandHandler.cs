using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.ResetUserMfa;

public sealed partial class ResetUserMfaCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IMfaService mfaService,
    ITokenService tokenService,
    ILogger<ResetUserMfaCommandHandler> logger)
    : IRequestHandler<ResetUserMfaCommand>
{
    public async Task Handle(ResetUserMfaCommand request, CancellationToken cancellationToken)
    {
        // El filtro de empresa restringe la búsqueda: el miembro de otra empresa se reporta igual que uno inexistente
        // (ADR-002, spec 009 R13).
        var target = await dbContext.TenantUsers.FirstOrDefaultAsync(tu => tu.Id == request.TenantUserId, cancellationToken)
            ?? throw new NotFoundException(TenancyErrors.TenantUserNotFound, request.TenantUserId);

        var actorRole = currentUser.Role!.Value;

        // Nadie se reinicia a sí mismo por esta vía: reiniciar se hace SIN contraseña ni código, y quien tuviera solo
        // un token robado podría quitarse (o quitar) el segundo factor. Para uno mismo está desactivar.
        if (target.UserId == currentUser.UserId)
        {
            Deny(AuthorizationLog.Reasons.CannotManageSelf, actorRole);
            throw new ForbiddenException(TenancyErrors.CannotManageSelf);
        }

        // La misma jerarquía que cambiar el rol o quitar a un miembro: un Admin no reinicia a otro Admin, y nadie
        // reinicia al Owner. Así un Admin comprometido no puede quitarle el segundo factor al dueño.
        if (!ManageableRoles.CanManage(actorRole, target.Role))
        {
            Deny(AuthorizationLog.Reasons.RoleNotManageable, actorRole);
            throw new ForbiddenException(TenancyErrors.RoleNotManageable);
        }

        // Todo o nada, y las sesiones también (spec 010 R14): quien llega a reiniciar el MFA lo hace por sospecha, y las
        // sesiones abiertas con el factor antiguo no deben sobrevivir.
        await mfaService.RemoveAllAsync(target.UserId, cancellationToken);
        await tokenService.DeleteRefreshTokensAsync(target.UserId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogReset(logger, target.Id, target.TenantId, currentUser.UserId);
    }

    private void Deny(string reason, TenantRole actorRole) =>
        AuthorizationLog.AccessDenied(logger, nameof(ResetUserMfaCommand), reason, currentUser.UserId, currentUser.TenantId, actorRole.ToString());

    // Solo Ids: nunca el nombre ni el email del miembro (ADR-009 R5).
    [LoggerMessage(EventId = SecurityEventIds.MfaResetByManager, Level = LogLevel.Warning,
        Message = "Segundo factor reiniciado por un gestor. TenantUserId {TenantUserId}, TenantId {TenantId}, reiniciado por {UserId}")]
    private static partial void LogReset(ILogger logger, Guid tenantUserId, Guid tenantId, Guid? userId);
}

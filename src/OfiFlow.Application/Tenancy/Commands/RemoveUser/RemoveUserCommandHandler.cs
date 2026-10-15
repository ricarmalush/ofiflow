using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.RemoveUser;

public sealed partial class RemoveUserCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IIdentityService identityService,
    ITokenService tokenService,
    ILogger<RemoveUserCommandHandler> logger)
    : IRequestHandler<RemoveUserCommand>
{
    public async Task Handle(RemoveUserCommand request, CancellationToken cancellationToken)
    {
        // El filtro de empresa restringe la búsqueda: el miembro de otra empresa se reporta igual que uno inexistente
        // (ADR-002, spec 009 R13).
        var target = await dbContext.TenantUsers.FirstOrDefaultAsync(tu => tu.Id == request.TenantUserId, cancellationToken)
            ?? throw new NotFoundException(TenancyErrors.TenantUserNotFound, request.TenantUserId);

        var actorRole = currentUser.Role!.Value;

        // R11: nadie se quita a sí mismo (dejaría la empresa sin quien la gestione).
        if (target.UserId == currentUser.UserId)
        {
            Deny(AuthorizationLog.Reasons.CannotManageSelf, actorRole);
            throw new ForbiddenException(TenancyErrors.CannotManageSelf);
        }

        // R9 y R12: hay que poder gestionar su rol. Un Admin no quita a otro Admin, y nadie quita al Owner (así toda
        // empresa conserva siempre a su Owner).
        if (!ManageableRoles.CanManage(actorRole, target.Role))
        {
            Deny(AuthorizationLog.Reasons.RoleNotManageable, actorRole);
            throw new ForbiddenException(TenancyErrors.RoleNotManageable);
        }

        // R14c: los trabajos sin terminar de esa persona quedan sin asignar (alguien tendrá que reasignarlos). Los
        // terminados o cancelados conservan la referencia como historial. El filtro de empresa ya limita esta consulta.
        var openJobs = await dbContext.Jobs
            .Where(j => j.AssignedTenantUserId == target.Id && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
            .ToListAsync(cancellationToken);

        foreach (var job in openJobs)
        {
            job.Unassign();
        }

        // R14d: hoy una cuenta pertenece a una sola empresa (D4), así que quitar la pertenencia deja la cuenta sin
        // empresa: se borran también la ficha y las credenciales (datos personales, y el email queda libre).
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == target.UserId, cancellationToken);
        if (user is not null)
        {
            dbContext.Users.Remove(user);
        }

        // Ninguna de estas tres confirma por su cuenta: todo se guarda de una vez, o no se guarda nada (R14).
        await identityService.DeleteUserAsync(target.UserId, cancellationToken);
        await tokenService.DeleteRefreshTokensAsync(target.UserId, cancellationToken);
        dbContext.TenantUsers.Remove(target);

        await dbContext.SaveChangesAsync(cancellationToken);

        LogUserRemoved(logger, target.Id, target.TenantId, currentUser.UserId, target.Role.ToString(), openJobs.Count);
    }

    private void Deny(string reason, TenantRole actorRole) =>
        AuthorizationLog.AccessDenied(logger, nameof(RemoveUserCommand), reason, currentUser.UserId, currentUser.TenantId, actorRole.ToString());

    // Solo Ids, el rol y un recuento: nunca el nombre ni el email de quien se quita (ADR-009 R5).
    [LoggerMessage(EventId = SecurityEventIds.UserRemoved, Level = LogLevel.Warning,
        Message = "Miembro quitado. TenantUserId {TenantUserId}, TenantId {TenantId}, quitado por {UserId}, rol {Role}, trabajos desasignados {UnassignedJobs}")]
    private static partial void LogUserRemoved(ILogger logger, Guid tenantUserId, Guid tenantId, Guid? userId, string role, int unassignedJobs);
}

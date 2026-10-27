using OfiFlow.Application.Common.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.AcceptInvitation;

public sealed partial class AcceptInvitationCommandHandler(
    IApplicationDbContext dbContext,
    IIdentityService identityService,
    TimeProvider timeProvider,
    ILogger<AcceptInvitationCommandHandler> logger)
    : IRequestHandler<AcceptInvitationCommand>
{
    public async Task Handle(AcceptInvitationCommand request, CancellationToken cancellationToken)
    {
        // Todavía no hay empresa activa (la persona no tiene cuenta): la invitación se busca por el hash de su
        // token, que es único y de 256 bits, así que esta consulta ignora el filtro de tenant de forma explícita
        // (excepción prevista y auditada: ADR-002, ADR-013; está en la lista blanca del test de arquitectura).
        var hash = OpaqueToken.Hash(request.Token);
        var invitation = await dbContext.Invitations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Desconocida, caducada, revocada o ya usada: el MISMO error para las cuatro (spec 009 R7). El motivo
        // real solo va al log, para un analista; quien lo intenta no aprende cuál de ellas es.
        if (invitation is null)
        {
            LogRejected(logger, "Unknown", null, null);
            throw new DomainException(InvitationErrors.Invalid);
        }

        var status = invitation.StatusAt(now);
        if (status != InvitationStatus.Pending)
        {
            LogRejected(logger, status.ToString(), invitation.Id, invitation.TenantId);
            throw new DomainException(InvitationErrors.Invalid);
        }

        // Una empresa suspendida no recibe gente nueva (spec 011 R15c). El MISMO error que con un token que no sirve: quien
        // lo intenta no aprende que la empresa existe ni que está suspendida; el motivo real solo va al log.
        if (await dbContext.IsSuspendedAsync(invitation.TenantId, cancellationToken))
        {
            LogRejected(logger, "TenantSuspended", invitation.Id, invitation.TenantId);
            throw new DomainException(InvitationErrors.Invalid);
        }

        var userId = Guid.NewGuid();

        // El email lo fijó quien invitó: aquí no se puede elegir otro (R4). Si entre tanto se registró alguien con ese
        // email, falla igual que el registro (R6) y la invitación NO se consume: nada se ha confirmado todavía.
        var identityResult = await identityService.CreateUserAsync(userId, invitation.Email.Value, request.Password, cancellationToken);
        if (!identityResult.Succeeded)
        {
            // Dos aceptaciones simultáneas del mismo token: las dos leyeron la invitación como pendiente, la primera confirmó
            // (cuenta y token consumido) y esta intenta crear una cuenta con el mismo email, que ya existe. Eso NO es «alguien
            // se registró con ese email»: es que el token ya no sirve, y recibe la misma respuesta que cualquier token que no
            // sirve (R7). Se distingue volviendo a leer el estado ACTUAL de la invitación (sin seguimiento: el que ya se cargó
            // sigue diciendo «pendiente»). Si sigue pendiente, sí fue otro registro con ese email y la invitación no se consume.
            var current = await dbContext.Invitations
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == invitation.Id, cancellationToken);

            if (current is null || current.StatusAt(now) != InvitationStatus.Pending)
            {
                LogRejected(logger, "Concurrent", invitation.Id, invitation.TenantId);
                throw new DomainException(InvitationErrors.Invalid);
            }

            LogRejected(logger, "EmailAlreadyRegistered", invitation.Id, invitation.TenantId);
            throw new IdentityOperationException(identityResult.Errors);
        }

        var user = User.Create(userId, request.Name, invitation.Email);
        var tenantUser = TenantUser.Create(invitation.TenantId, userId, invitation.Role);
        invitation.Accept(now);

        dbContext.Users.Add(user);
        dbContext.TenantUsers.Add(tenantUser);

        try
        {
            // Una única confirmación: cuenta, ficha, pertenencia y token consumido, o nada (R8).
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Dos aceptaciones simultáneas del mismo token: la base de datos dejó pasar a la primera y rechaza a esta.
            // Lo rechaza de una de dos formas, según qué compruebe antes: por la versión de la invitación
            // (DbUpdateConcurrencyException) o, más a menudo, porque las dos intentan crear una cuenta con el MISMO email
            // y el índice único lo impide (DbUpdateException). Las dos significan lo mismo, "este token ya no sirve", y
            // reciben la misma respuesta. Se capturan las dos juntas: capturar solo la de concurrencia dejaba escapar
            // la otra como un 500 (lo cazó el test contra SQL Server real, no los de en memoria).
            LogRejected(logger, "Concurrent", invitation.Id, invitation.TenantId);
            throw new DomainException(InvitationErrors.Invalid);
        }

        LogAccepted(logger, invitation.Id, invitation.TenantId, userId, invitation.Role.ToString());
    }

    // Nunca el token, su hash ni el email (ADR-009 R5). La IP la añade el scope de la API.
    [LoggerMessage(EventId = SecurityEventIds.InvitationRejected, Level = LogLevel.Warning,
        Message = "Invitación rechazada. Motivo {Reason}, InvitationId {InvitationId}, TenantId {TenantId}")]
    private static partial void LogRejected(ILogger logger, string reason, Guid? invitationId, Guid? tenantId);

    [LoggerMessage(EventId = SecurityEventIds.InvitationAccepted, Level = LogLevel.Information,
        Message = "Invitación aceptada. InvitationId {InvitationId}, TenantId {TenantId}, nuevo UserId {UserId}, rol {Role}")]
    private static partial void LogAccepted(ILogger logger, Guid invitationId, Guid tenantId, Guid userId, string role);
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Platform.Commands.ReactivateTenant;

public sealed partial class ReactivateTenantCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IPlatformAudit platformAudit,
    TimeProvider timeProvider,
    ILogger<ReactivateTenantCommandHandler> logger)
    : IRequestHandler<ReactivateTenantCommand>
{
    public async Task Handle(ReactivateTenantCommand request, CancellationToken cancellationToken)
    {
        var operatorUserId = currentUser.UserId ?? throw new ForbiddenException(TenancyErrors.Forbidden);

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException(TenancyErrors.TenantNotFound, request.TenantId);

        // Una empresa que no está suspendida lanza el error de negocio ANTES de añadir nada: no queda fila de auditoría.
        tenant.Reactivate();

        // R17: la reactivación y su fila de auditoría se confirman juntas (ver SuspendTenantCommandHandler).
        platformAudit.Add(operatorUserId, PlatformAuditActions.TenantReactivated, tenant.Id, timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra petición la reactivó (o la volvió a suspender) a la vez: el estado ya cambió. Mismo resultado que llegar un instante después.
            throw new DomainException(TenancyErrors.TenantNotSuspended);
        }

        LogReactivated(logger, operatorUserId, tenant.Id);
    }

    [LoggerMessage(EventId = SecurityEventIds.PlatformTenantReactivated, Level = LogLevel.Warning,
        Message = "Empresa reactivada por un operador de la plataforma. UserId {UserId}. TenantId {TenantId}")]
    private static partial void LogReactivated(ILogger logger, Guid userId, Guid tenantId);
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Platform.Commands.SuspendTenant;

public sealed partial class SuspendTenantCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IPlatformAudit platformAudit,
    TimeProvider timeProvider,
    ILogger<SuspendTenantCommandHandler> logger)
    : IRequestHandler<SuspendTenantCommand>
{
    public async Task Handle(SuspendTenantCommand request, CancellationToken cancellationToken)
    {
        // El Id del operador sale del token ya validado, nunca de la petición. Sin él no hay quien deje rastro: ante la duda, se deniega.
        var operatorUserId = currentUser.UserId ?? throw new ForbiddenException(TenancyErrors.Forbidden);

        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException(TenancyErrors.TenantNotFound, request.TenantId);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Una empresa ya suspendida lanza el error de negocio ANTES de añadir nada: no queda una segunda fila de auditoría.
        tenant.Suspend(now);

        // R17: la fila de auditoría y la suspensión se confirman JUNTAS, en el mismo SaveChanges. Si una no se puede escribir, no se
        // escribe ninguna: ni una empresa suspendida sin rastro, ni un rastro de algo que no ocurrió.
        platformAudit.Add(operatorUserId, PlatformAuditActions.TenantSuspended, tenant.Id, now);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Dos operadores (o dos peticiones) suspendieron la misma empresa a la vez: gana una y la otra se encuentra el estado ya
            // cambiado. Es el mismo resultado que si hubiera llegado un instante después: la empresa ya estaba suspendida.
            throw new DomainException(TenancyErrors.TenantAlreadySuspended);
        }

        LogSuspended(logger, operatorUserId, tenant.Id);
    }

    [LoggerMessage(EventId = SecurityEventIds.PlatformTenantSuspended, Level = LogLevel.Warning,
        Message = "Empresa suspendida por un operador de la plataforma. UserId {UserId}. TenantId {TenantId}")]
    private static partial void LogSuspended(ILogger logger, Guid userId, Guid tenantId);
}

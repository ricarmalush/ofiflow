using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.VerifyMfaChallenge;

public sealed partial class VerifyMfaChallengeCommandHandler(
    IApplicationDbContext dbContext,
    IMfaService mfaService,
    ITokenService tokenService,
    ILogger<VerifyMfaChallengeCommandHandler> logger)
    : IRequestHandler<VerifyMfaChallengeCommand, AuthResultDto?>
{
    public async Task<AuthResultDto?> Handle(VerifyMfaChallengeCommand request, CancellationToken cancellationToken)
    {
        // La forma exacta se comprueba antes de hashear ni buscar nada (ADR-009 R3), pero un fallo aquí da la misma
        // respuesta que cualquier otro (null → 401, R13): quien lo intenta no aprende cuál fue.
        if (!OpaqueToken.IsWellFormed(request.Challenge) || !SecondFactorCode.IsWellFormed(request.Code))
        {
            LogRejected(logger, "MalformedInput");
            return null;
        }

        var redemption = await mfaService.RedeemChallengeAsync(OpaqueToken.Hash(request.Challenge), request.Code, ChallengeAudience.Tenant, cancellationToken);
        if (!redemption.Succeeded)
        {
            return null;
        }

        // Aún no hay empresa activa (la petición es anónima): la pertenencia se busca saltándose el filtro de empresa,
        // igual que el login (ADR-002). La sesión se emite para la empresa DE LA PERTENENCIA, nunca para una que mande el cliente.
        var tenantUser = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(tu => tu.UserId == redemption.UserId, cancellationToken);

        if (tenantUser is null)
        {
            LogLoginWithoutTenantMembership(logger, redemption.UserId);
            return null;
        }

        // La empresa se suspendió entre la contraseña y el código (spec 011 R15a). La persona ya probó quién es (contraseña y
        // código), así que se le dice la verdad con su código; el login ya la habría parado en el primer paso si lo estuviera.
        if (await dbContext.IsSuspendedAsync(tenantUser.TenantId, cancellationToken))
        {
            LogBlockedBySuspension(logger, redemption.UserId, tenantUser.TenantId);
            throw new ForbiddenException(TenancyErrors.TenantSuspended);
        }

        return await tokenService.IssueTokensAsync(redemption.UserId, tenantUser.TenantId, tenantUser.Role, cancellationToken);
    }

    // Sin UserId: no se sabe de quién es (el valor no existe o tiene un formato imposible).
    [LoggerMessage(EventId = SecurityEventIds.MfaVerificationFailed, Level = LogLevel.Warning,
        Message = "Segundo paso del login rechazado. Motivo {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(EventId = SecurityEventIds.TenantSuspendedAccessDenied, Level = LogLevel.Warning,
        Message = "Segundo paso del login denegado: la empresa está suspendida. UserId {UserId}, TenantId {TenantId}")]
    private static partial void LogBlockedBySuspension(ILogger logger, Guid userId, Guid tenantId);

    [LoggerMessage(EventId = SecurityEventIds.LoginWithoutTenantMembership, Level = LogLevel.Warning,
        Message = "Segundo factor superado pero sin pertenencia a ningún tenant. UserId {UserId}")]
    private static partial void LogLoginWithoutTenantMembership(ILogger logger, Guid userId);
}

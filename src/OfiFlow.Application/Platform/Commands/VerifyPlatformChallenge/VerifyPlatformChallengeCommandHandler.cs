using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity.Mfa;

namespace OfiFlow.Application.Platform.Commands.VerifyPlatformChallenge;

public sealed partial class VerifyPlatformChallengeCommandHandler(
    IApplicationDbContext dbContext,
    IMfaService mfaService,
    IPlatformOperators platformOperators,
    IPlatformTokenService platformTokenService,
    IPlatformAudit platformAudit,
    TimeProvider timeProvider,
    ILogger<VerifyPlatformChallengeCommandHandler> logger)
    : IRequestHandler<VerifyPlatformChallengeCommand, PlatformSessionDto?>
{
    public async Task<PlatformSessionDto?> Handle(VerifyPlatformChallengeCommand request, CancellationToken cancellationToken)
    {
        // La forma exacta se comprueba antes de hashear ni buscar nada (ADR-009 R3), pero un fallo aquí da la misma respuesta que
        // cualquier otro (null → 401, R6): quien lo intenta no aprende cuál fue.
        if (!OpaqueToken.IsWellFormed(request.Challenge) || !SecondFactorCode.IsWellFormed(request.Code))
        {
            LogRejected(logger, "MalformedInput", null);
            return null;
        }

        // R9: solo se canjea un desafío NACIDO para la plataforma. Uno de empresa se trata como uno inventado (ni se comprueba el
        // código ni se gasta ningún intento). Cada código erróneo suma al desafío y al bloqueo de la cuenta, como en la empresa.
        var redemption = await mfaService.RedeemChallengeAsync(OpaqueToken.Hash(request.Challenge), request.Code, ChallengeAudience.Platform, cancellationToken);
        if (!redemption.Succeeded)
        {
            return null;
        }

        // R10: se vuelve a comprobar que sigue siendo un operador activo. Pudo darse de baja entre la contraseña y el código.
        if (!await platformOperators.IsActiveOperatorAsync(redemption.UserId, cancellationToken))
        {
            LogRejected(logger, "NotAnActiveOperator", redemption.UserId);
            return null;
        }

        // El ÚNICO sitio que emite una sesión de plataforma (R8; lo fija un test de arquitectura). La firma es pura: no toca
        // la base de datos, y no hay refresh token.
        var session = platformTokenService.IssuePlatformSession(redemption.UserId);

        // R17: la fila de auditoría se confirma ANTES de devolver el token. Si no se puede escribir, el token no sale.
        platformAudit.Add(redemption.UserId, PlatformAuditActions.SessionIssued, tenantId: null, timeProvider.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogSessionIssued(logger, redemption.UserId);

        return session;
    }

    // El motivo real del fallo del canje (desafío desconocido, caducado, agotado, código erróneo...) lo registra IMfaService con 1504.
    [LoggerMessage(EventId = SecurityEventIds.PlatformLoginRejected, Level = LogLevel.Warning,
        Message = "Segundo paso del inicio de sesión de plataforma rechazado. Motivo {Reason}. UserId {UserId}")]
    private static partial void LogRejected(ILogger logger, string reason, Guid? userId);

    [LoggerMessage(EventId = SecurityEventIds.PlatformSessionIssued, Level = LogLevel.Warning,
        Message = "Sesión de operador de la plataforma emitida (sin refresh token). UserId {UserId}")]
    private static partial void LogSessionIssued(ILogger logger, Guid userId);
}

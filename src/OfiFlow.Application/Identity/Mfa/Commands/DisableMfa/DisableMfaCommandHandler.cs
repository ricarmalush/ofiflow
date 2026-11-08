using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;

public sealed partial class DisableMfaCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IIdentityService identityService,
    IMfaService mfaService,
    ITokenService tokenService,
    ISecondFactorRequirement secondFactorRequirement,
    ILogger<DisableMfaCommandHandler> logger)
    : IRequestHandler<DisableMfaCommand>
{
    public async Task Handle(DisableMfaCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;

        // Lo primero (spec 022 R7): con un rol que exige el segundo factor no se puede desactivar, y se dice ANTES de pedir la contraseña ni el código: no hay nada que comprobar, no se puede,
        // y así un intento no gasta intentos del bloqueo de cuenta ni da pistas sobre la contraseña. Se decide con el rol EFECTIVO (el de la base de datos; ya verificado por el
        // AuthorizationBehavior), nunca con el del token; sin rol, se niega (fail-closed). Quien ya no tiene un rol que lo exige (lo degradaron) sigue de largo y puede desactivarlo.
        if (currentUser.Role is not { } role || secondFactorRequirement.IsRequiredFor(role))
        {
            LogDeniedForRole(logger, userId, currentUser.TenantId, currentUser.Role?.ToString() ?? "(sin rol)");
            throw new BusinessRuleException(MfaErrors.RequiredForRole);
        }

        // Primero la contraseña (como al pedir el secreto), luego que haya algo que desactivar, luego el código.
        // Los fallos de la contraseña y del código cuentan para el bloqueo de cuenta (spec 010 R7).
        if (!await identityService.ConfirmPasswordAsync(userId, request.Password, cancellationToken))
        {
            throw new BusinessRuleException(MfaErrors.InvalidPassword);
        }

        if (!(await mfaService.GetStatusAsync(userId, cancellationToken)).IsEnabled)
        {
            throw new BusinessRuleException(MfaErrors.NotEnabled);
        }

        if (!(await mfaService.VerifySecondFactorAsync(userId, request.Code, cancellationToken)).Valid)
        {
            throw new BusinessRuleException(MfaErrors.InvalidCode);
        }

        // Todo o nada: se quita el secreto, los códigos y los desafíos, y se cierran las sesiones (R14), en un único SaveChanges.
        await mfaService.RemoveAllAsync(userId, cancellationToken);
        await tokenService.DeleteRefreshTokensAsync(userId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogDisabled(logger, userId);
    }

    [LoggerMessage(EventId = SecurityEventIds.MfaDisabled, Level = LogLevel.Warning,
        Message = "Segundo factor desactivado; sesiones de la cuenta cerradas. UserId {UserId}")]
    private static partial void LogDisabled(ILogger logger, Guid userId);

    [LoggerMessage(EventId = SecurityEventIds.MfaDisableDeniedForRole, Level = LogLevel.Warning,
        Message = "Desactivar el segundo factor denegado: el rol lo exige. UserId {UserId}, TenantId {TenantId}, rol {Role}")]
    private static partial void LogDeniedForRole(ILogger logger, Guid userId, Guid? tenantId, string role);
}

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
    ILogger<DisableMfaCommandHandler> logger)
    : IRequestHandler<DisableMfaCommand>
{
    public async Task Handle(DisableMfaCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;

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
}

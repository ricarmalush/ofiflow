using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;

public sealed partial class EnableMfaCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IMfaService mfaService,
    ITokenService tokenService,
    ILogger<EnableMfaCommandHandler> logger)
    : IRequestHandler<EnableMfaCommand, RecoveryCodesDto>
{
    public async Task<RecoveryCodesDto> Handle(EnableMfaCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var status = await mfaService.GetStatusAsync(userId, cancellationToken);

        if (status.IsEnabled)
        {
            throw new BusinessRuleException(MfaErrors.AlreadyEnabled);
        }

        if (!status.SetupPending)
        {
            throw new BusinessRuleException(MfaErrors.SetupRequired);
        }

        var codes = RecoveryCodes.GenerateBatch(AuthenticationPolicy.RecoveryCodeCount);

        var outcome = await mfaService.ConfirmSetupAsync(userId, request.Code, codes.Select(code => code.Hash).ToList(), cancellationToken);

        if (outcome == SetupOutcome.NoPendingSetup)
        {
            throw new BusinessRuleException(MfaErrors.SetupRequired);
        }

        if (outcome == SetupOutcome.InvalidCode)
        {
            throw new BusinessRuleException(MfaErrors.InvalidCode);
        }

        // R14: cambia cómo se demuestra quién eres, así que las sesiones abiertas con las reglas antiguas se cierran.
        // Todo en el mismo SaveChanges: o se activa y se cierran las sesiones, o no pasa nada.
        await tokenService.DeleteRefreshTokensAsync(userId, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Dos confirmaciones a la vez con códigos válidos: solo una puede activarlo. La otra se encuentra el MFA
            // ya activo (una violación de unicidad o de versión de fila), no un error del servidor.
            throw new BusinessRuleException(MfaErrors.AlreadyEnabled);
        }

        LogEnabled(logger, userId);

        return new RecoveryCodesDto(codes.Select(code => code.Code).ToList());
    }

    [LoggerMessage(EventId = SecurityEventIds.MfaEnabled, Level = LogLevel.Warning,
        Message = "Segundo factor activado; sesiones de la cuenta cerradas. UserId {UserId}")]
    private static partial void LogEnabled(ILogger logger, Guid userId);
}

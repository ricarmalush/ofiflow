using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;

public sealed partial class RegenerateRecoveryCodesCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IIdentityService identityService,
    IMfaService mfaService,
    ILogger<RegenerateRecoveryCodesCommandHandler> logger)
    : IRequestHandler<RegenerateRecoveryCodesCommand, RecoveryCodesDto>
{
    public async Task<RecoveryCodesDto> Handle(RegenerateRecoveryCodesCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;

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

        var codes = RecoveryCodes.GenerateBatch(AuthenticationPolicy.RecoveryCodeCount);

        await mfaService.ReplaceRecoveryCodesAsync(userId, codes.Select(code => code.Hash).ToList(), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogRegenerated(logger, userId);

        return new RecoveryCodesDto(codes.Select(code => code.Code).ToList());
    }

    [LoggerMessage(EventId = SecurityEventIds.RecoveryCodesRegenerated, Level = LogLevel.Warning,
        Message = "Códigos de recuperación regenerados; los anteriores ya no valen. UserId {UserId}")]
    private static partial void LogRegenerated(ILogger logger, Guid userId);
}

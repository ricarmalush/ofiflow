using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;

public sealed partial class StartMfaSetupCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUser currentUser,
    IIdentityService identityService,
    IMfaService mfaService,
    ILogger<StartMfaSetupCommandHandler> logger)
    : IRequestHandler<StartMfaSetupCommand, MfaSetupDto>
{
    public async Task<MfaSetupDto> Handle(StartMfaSetupCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;

        // La contraseña va PRIMERO: así quien solo tiene un token robado no averigua, ni siquiera con un error
        // distinto, si la cuenta ya tiene el MFA activo. Un fallo aquí cuenta para el bloqueo de cuenta.
        if (!await identityService.ConfirmPasswordAsync(userId, request.Password, cancellationToken))
        {
            throw new BusinessRuleException(MfaErrors.InvalidPassword);
        }

        if ((await mfaService.GetStatusAsync(userId, cancellationToken)).IsEnabled)
        {
            throw new BusinessRuleException(MfaErrors.AlreadyEnabled);
        }

        var secret = TotpSecret.Generate();
        var accountName = await mfaService.BeginSetupAsync(userId, secret, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogSetupStarted(logger, userId);

        return new MfaSetupDto(Base32.Encode(secret), TotpSecret.BuildUri(AuthenticationPolicy.TotpIssuer, accountName, secret));
    }

    [LoggerMessage(EventId = SecurityEventIds.MfaSetupStarted, Level = LogLevel.Information,
        Message = "Secreto del segundo factor pedido. UserId {UserId}")]
    private static partial void LogSetupStarted(ILogger logger, Guid userId);
}

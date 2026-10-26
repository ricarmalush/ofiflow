using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;

namespace OfiFlow.Application.Platform.Commands.PlatformLogin;

public sealed partial class PlatformLoginCommandHandler(
    IApplicationDbContext dbContext,
    IIdentityService identityService,
    IPlatformOperators platformOperators,
    IMfaService mfaService,
    TimeProvider timeProvider,
    ILogger<PlatformLoginCommandHandler> logger)
    : IRequestHandler<PlatformLoginCommand, MfaChallengeDto?>
{
    public async Task<MfaChallengeDto?> Handle(PlatformLoginCommand request, CancellationToken cancellationToken)
    {
        // Misma comprobación de contraseña, con bloqueo de cuenta y con el mismo coste para todos los casos (R6): un email
        // desconocido o una cuenta bloqueada hacen una verificación completa contra un hash ficticio. Es el ÚNICO sitio desde
        // el que se comprueba una contraseña (IIdentityService): así no se puede olvidar el bloqueo.
        var authentication = await identityService.AuthenticateAsync(request.Email, request.Password, cancellationToken);
        if (authentication is null)
        {
            LogRejected(logger, Reasons.InvalidCredentials, null);
            return null;
        }

        var userId = authentication.UserId;

        // R6: una cuenta de EMPRESA con su contraseña correcta, o un operador dado de baja, reciben exactamente lo mismo que quien se
        // equivocó de contraseña. El motivo real solo va al registro.
        if (!await platformOperators.IsActiveOperatorAsync(userId, cancellationToken))
        {
            LogRejected(logger, Reasons.NotAnActiveOperator, userId);
            return null;
        }

        // R5 y D4: no hay operador sin segundo factor. Si faltara (no debería: el alta lo exige), la contraseña sola no da nada.
        if (!authentication.SecondFactorRequired)
        {
            LogRejected(logger, Reasons.NoSecondFactor, userId);
            return null;
        }

        // Esta clase no recibe ningún servicio de tokens: el login de plataforma NUNCA devuelve una sesión, solo un desafío que
        // nace para el mundo de la plataforma (R9) y solo se canjea ahí.
        var (challenge, challengeHash) = OpaqueToken.Generate();
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime + AuthenticationPolicy.ChallengeLifetime;

        await mfaService.CreateChallengeAsync(userId, challengeHash, expiresAt, ChallengeAudience.Platform, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MfaChallengeDto(challenge, expiresAt);
    }

    /// <summary>Los motivos reales de un rechazo. Solo van al registro: quien llama recibe siempre el mismo 401.</summary>
    internal static class Reasons
    {
        public const string InvalidCredentials = "InvalidCredentials";
        public const string NotAnActiveOperator = "NotAnActiveOperator";
        public const string NoSecondFactor = "NoSecondFactor";
    }

    [LoggerMessage(EventId = SecurityEventIds.PlatformLoginRejected, Level = LogLevel.Warning,
        Message = "Inicio de sesión de plataforma rechazado. Motivo {Reason}. UserId {UserId}")]
    private static partial void LogRejected(ILogger logger, string reason, Guid? userId);
}

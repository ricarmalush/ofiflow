using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.Login;

public sealed partial class LoginCommandHandler(
    IApplicationDbContext dbContext,
    IIdentityService identityService,
    ITokenService tokenService,
    IMfaService mfaService,
    TimeProvider timeProvider,
    ILogger<LoginCommandHandler> logger)
    : IRequestHandler<LoginCommand, LoginResult>
{
    public async Task<LoginResult> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var authentication = await identityService.AuthenticateAsync(request.Email, request.Password, cancellationToken);
        if (authentication is null)
        {
            return new LoginResult.Rejected();
        }

        var userId = authentication.UserId;

        // Todavía no hay tenant activo — es justo lo que este Handler determina — por eso
        // esta consulta ignora el Global Query Filter de forma explícita (excepción prevista
        // y auditada en ADR-002).
        var tenantUser = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(tu => tu.UserId == userId, cancellationToken);

        if (tenantUser is null)
        {
            // Credenciales correctas pero sin empresa: no debería ocurrir (el registro crea
            // ambas cosas a la vez), así que es una anomalía que merece quedar registrada.
            LogLoginWithoutTenantMembership(logger, userId);
            return new LoginResult.Rejected();
        }

        // Empresa suspendida (spec 011 R15a): las credenciales eran correctas, así que la persona puede saberlo (403 con su
        // código) y no se la confunde con quien falló la contraseña. NO cuenta como intento fallido: AuthenticateAsync ya
        // dejó el contador como en cualquier acierto. Va ANTES de crear el desafío: sin sesión posible no hay segundo paso.
        if (await dbContext.IsSuspendedAsync(tenantUser.TenantId, cancellationToken))
        {
            LogBlockedBySuspension(logger, userId, tenantUser.TenantId);
            throw new ForbiddenException(TenancyErrors.TenantSuspended);
        }

        if (authentication.SecondFactorRequired)
        {
            // La contraseña NO da sesión (spec 010 R10, R12): se entrega un desafío y nada más. Esta rama no llama
            // a ITokenService, y un test de arquitectura impide que nadie más emita sesiones sin pasar el segundo factor.
            var (challenge, challengeHash) = OpaqueToken.Generate();
            var expiresAt = timeProvider.GetUtcNow().UtcDateTime + AuthenticationPolicy.ChallengeLifetime;

            await mfaService.CreateChallengeAsync(userId, challengeHash, expiresAt, ChallengeAudience.Tenant, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new LoginResult.SecondFactorRequired(new MfaChallengeDto(challenge, expiresAt));
        }

        return new LoginResult.Authenticated(
            await tokenService.IssueTokensAsync(userId, tenantUser.TenantId, tenantUser.Role, cancellationToken));
    }

    [LoggerMessage(EventId = SecurityEventIds.TenantSuspendedAccessDenied, Level = LogLevel.Warning,
        Message = "Login denegado: la empresa está suspendida. UserId {UserId}, TenantId {TenantId}")]
    private static partial void LogBlockedBySuspension(ILogger logger, Guid userId, Guid tenantId);

    [LoggerMessage(EventId = SecurityEventIds.LoginWithoutTenantMembership, Level = LogLevel.Warning,
        Message = "Login con credenciales válidas pero sin pertenencia a ningún tenant. UserId {UserId}")]
    private static partial void LogLoginWithoutTenantMembership(ILogger logger, Guid userId);
}

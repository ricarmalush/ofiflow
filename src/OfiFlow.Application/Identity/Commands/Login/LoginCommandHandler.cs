using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Identity.Commands.Login;

public sealed partial class LoginCommandHandler(
    IApplicationDbContext dbContext,
    IIdentityService identityService,
    ITokenService tokenService,
    ILogger<LoginCommandHandler> logger)
    : IRequestHandler<LoginCommand, AuthResultDto?>
{
    public async Task<AuthResultDto?> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var userId = await identityService.ValidateCredentialsAsync(request.Email, request.Password, cancellationToken);
        if (userId is null)
        {
            return null;
        }

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
            LogLoginWithoutTenantMembership(logger, userId.Value);
            return null;
        }

        return await tokenService.IssueTokensAsync(userId.Value, tenantUser.TenantId, tenantUser.Role, cancellationToken);
    }

    [LoggerMessage(EventId = SecurityEventIds.LoginWithoutTenantMembership, Level = LogLevel.Warning,
        Message = "Login con credenciales válidas pero sin pertenencia a ningún tenant. UserId {UserId}")]
    private static partial void LogLoginWithoutTenantMembership(ILogger logger, Guid userId);
}

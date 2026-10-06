using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Behaviors;

/// <summary>
/// Comprueba el permiso de cada operación antes de que llegue a su handler y antes que la
/// validación (ADR-012 R3): quien no puede hacer algo no recibe información sobre sus datos.
/// Es fail-closed: una operación sin <see cref="RequiresPermissionAttribute"/> ni
/// <see cref="AllowAnonymousRequestAttribute"/>, o con las dos a la vez, se deniega.
/// </summary>
public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser currentUser, ILogger<AuthorizationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    // Se lee una sola vez por tipo de operación (un campo estático de un genérico cerrado).
    private static readonly Declaration Declared = Declaration.Of(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (Declared is { Anonymous: true, Permissions.Length: 0 })
        {
            return await next();
        }

        var reason = DenialReason();
        if (reason is not null)
        {
            AuthorizationLog.AccessDenied(logger, Declared.Operation, reason, currentUser.UserId, currentUser.TenantId, currentUser.Role?.ToString());
            throw new ForbiddenException(TenancyErrors.Forbidden);
        }

        return await next();
    }

    private string? DenialReason()
    {
        if (Declared.Permissions.Length == 0 && !Declared.Anonymous)
        {
            return AuthorizationLog.Reasons.NotDeclared;
        }

        if (Declared.Anonymous)
        {
            // Anónima y con permiso a la vez: un error de quien la escribió, no un acceso legítimo.
            return AuthorizationLog.Reasons.Misdeclared;
        }

        if (currentUser.Role is not { } role)
        {
            return AuthorizationLog.Reasons.NoValidRole;
        }

        return Declared.Permissions.All(permission => RolePermissions.Has(role, permission))
            ? null
            : AuthorizationLog.Reasons.MissingPermission;
    }

    private sealed record Declaration(string Operation, bool Anonymous, Permission[] Permissions)
    {
        public static Declaration Of(Type request) => new(
            request.Name,
            request.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true),
            [.. ((RequiresPermissionAttribute[])request.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true))
                .Select(attribute => attribute.Permission)]);
    }
}

/// <summary>El registro de seguridad de las denegaciones. Nunca incluye datos de la petición (ADR-009 R5).</summary>
internal static partial class AuthorizationLog
{
    public static class Reasons
    {
        public const string NotDeclared = "NotDeclared";
        public const string Misdeclared = "Misdeclared";
        public const string NoValidRole = "NoValidRole";
        public const string MissingPermission = "MissingPermission";
    }

    // La IP la añade el scope de RequestLogScopeMiddleware.
    [LoggerMessage(EventId = SecurityEventIds.AccessDenied, Level = LogLevel.Warning,
        Message = "Acceso denegado a {Operation} ({Reason}). UserId {UserId}, TenantId {TenantId}, rol {Role}")]
    public static partial void AccessDenied(ILogger logger, string operation, string reason, Guid? userId, Guid? tenantId, string? role);
}

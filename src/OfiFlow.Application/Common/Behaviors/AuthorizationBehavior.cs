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
/// <remarks>
/// Desde la spec 009 (ADR-013) revalida la pertenencia <b>en cada petición</b>: lee en la base de datos si la persona sigue
/// en la empresa y con qué rol, y decide con ESE rol, no con el del token. Así quitar a alguien o degradarlo surte efecto
/// en su siguiente petición y no a los 30 minutos. Esto cambia lo que decía ADR-012 ("la autorización no consulta la base
/// de datos"), a cambio de una consulta por el índice único. Las operaciones mal declaradas se deniegan ANTES de consultar
/// nada, y las anónimas no consultan.
/// </remarks>
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

        // Una operación mal declarada es un error de quien la escribió: se deniega sin gastar una consulta.
        var declarationProblem = DeclarationProblem();
        if (declarationProblem is not null)
        {
            Deny(declarationProblem);
        }

        // Revalidación (R16): ¿sigue perteneciendo a la empresa? Si no, el token todavía válido ya no sirve de nada.
        if (!await currentUser.VerifyMembershipAsync(cancellationToken))
        {
            AuthorizationLog.MembershipRevoked(logger, Declared.Operation, currentUser.UserId, currentUser.TenantId);
            throw new ForbiddenException(TenancyErrors.Forbidden);
        }

        // El rol efectivo es el de la base de datos, no el del token.
        var role = currentUser.Role!.Value;
        if (!Declared.Permissions.All(permission => RolePermissions.Has(role, permission)))
        {
            Deny(AuthorizationLog.Reasons.MissingPermission);
        }

        return await next();
    }

    private string? DeclarationProblem()
    {
        if (Declared.Permissions.Length == 0 && !Declared.Anonymous)
        {
            return AuthorizationLog.Reasons.NotDeclared;
        }

        // Anónima y con permiso a la vez: un error de quien la escribió, no un acceso legítimo.
        return Declared.Anonymous ? AuthorizationLog.Reasons.Misdeclared : null;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Deny(string reason)
    {
        AuthorizationLog.AccessDenied(logger, Declared.Operation, reason, currentUser.UserId, currentUser.TenantId, currentUser.Role?.ToString());
        throw new ForbiddenException(TenancyErrors.Forbidden);
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
        public const string MissingPermission = "MissingPermission";

        // La jerarquía de roles (spec 009): tiene el permiso, pero no puede gestionar a ese rol.
        public const string RoleNotManageable = "RoleNotManageable";
        public const string CannotManageSelf = "CannotManageSelf";
    }

    // La IP la añade el scope de RequestLogScopeMiddleware.
    [LoggerMessage(EventId = SecurityEventIds.AccessDenied, Level = LogLevel.Warning,
        Message = "Acceso denegado a {Operation} ({Reason}). UserId {UserId}, TenantId {TenantId}, rol {Role}")]
    public static partial void AccessDenied(ILogger logger, string operation, string reason, Guid? userId, Guid? tenantId, string? role);

    [LoggerMessage(EventId = SecurityEventIds.MembershipRevoked, Level = LogLevel.Warning,
        Message = "Token válido sin pertenencia a la empresa en {Operation}. UserId {UserId}, TenantId {TenantId}")]
    public static partial void MembershipRevoked(ILogger logger, string operation, Guid? userId, Guid? tenantId);
}

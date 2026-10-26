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
/// Es fail-closed: una operación sin <see cref="RequiresPermissionAttribute"/>, <see cref="AllowAnonymousRequestAttribute"/>
/// ni <see cref="RequiresPlatformOperatorAttribute"/>, o con más de un tipo a la vez, se deniega.
/// </summary>
/// <remarks>
/// Desde la spec 009 (ADR-013) revalida la pertenencia <b>en cada petición</b>: lee en la base de datos si la persona sigue
/// en la empresa y con qué rol, y decide con ESE rol, no con el del token. Así quitar a alguien o degradarlo surte efecto
/// en su siguiente petición y no a los 30 minutos. Esto cambia lo que decía ADR-012 ("la autorización no consulta la base
/// de datos"), a cambio de una consulta por el índice único. Las operaciones mal declaradas se deniegan ANTES de consultar
/// nada, y las anónimas no consultan.
/// <para>
/// Desde la spec 011 (ADR-016) hay un <b>tercer tipo de declaración</b>, <see cref="RequiresPlatformOperatorAttribute"/>: la
/// operación es de la plataforma y exige una sesión de plataforma de un operador que sigue activo, comprobado en la base de
/// datos en cada petición (R10). Es excluyente con los otros dos: los permisos son de una empresa y el operador no tiene
/// ninguna. Una operación de plataforma nunca consulta la pertenencia a una empresa.
/// </para>
/// </remarks>
public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser currentUser, ILogger<AuthorizationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    // Se lee una sola vez por tipo de operación (un campo estático de un genérico cerrado).
    private static readonly Declaration Declared = Declaration.Of(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Solo es anónima la que declara ESO y nada más: una anónima que además es de plataforma está mal declarada y se
        // deniega más abajo (si este atajo no mirara lo otro, dejaría pasar sin control una operación de plataforma).
        if (Declared is { Anonymous: true, Permissions.Length: 0, PlatformOperator: false })
        {
            return await next();
        }

        // Una operación mal declarada es un error de quien la escribió: se deniega sin gastar una consulta.
        var declarationProblem = DeclarationProblem();
        if (declarationProblem is not null)
        {
            Deny(declarationProblem);
        }

        // Operación de plataforma (spec 011 R10 y R14): ni rol ni pertenencia, sino una sesión de plataforma de un operador activo.
        if (Declared.PlatformOperator)
        {
            if (!currentUser.IsPlatformSession)
            {
                // Un token que no es de plataforma (en la práctica, uno de empresa que llegó por una ruta mal declarada: el esquema
                // de plataforma ya lo habría rechazado antes con un 401). Defensa en profundidad.
                Deny(AuthorizationLog.Reasons.NotPlatformSession);
            }

            if (!await currentUser.VerifyPlatformOperatorAsync(cancellationToken))
            {
                AuthorizationLog.PlatformSessionRevoked(logger, Declared.Operation, currentUser.UserId);
                throw new ForbiddenException(TenancyErrors.Forbidden);
            }

            return await next();
        }

        // Revalidación (R16): ¿sigue perteneciendo a la empresa? Si no, el token todavía válido ya no sirve de nada.
        if (!await currentUser.VerifyMembershipAsync(cancellationToken))
        {
            // Sigue siendo miembro, pero su empresa está suspendida (spec 011 R15d): se distingue de «ya no pertenece» para
            // dar el código correcto (la persona está autenticada: puede saberlo) y dejar el evento correcto.
            if (currentUser.TenantSuspended)
            {
                AuthorizationLog.TenantSuspended(logger, Declared.Operation, currentUser.UserId, currentUser.TenantId);
                throw new ForbiddenException(TenancyErrors.TenantSuspended);
            }

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
        // Exactamente un tipo de declaración (permiso, anónima o plataforma): ninguno es NotDeclared, más de uno es Misdeclared.
        var kinds = (Declared.Permissions.Length > 0 ? 1 : 0) + (Declared.Anonymous ? 1 : 0) + (Declared.PlatformOperator ? 1 : 0);

        return kinds switch
        {
            0 => AuthorizationLog.Reasons.NotDeclared,

            // Un error de quien la escribió, no un acceso legítimo.
            > 1 => AuthorizationLog.Reasons.Misdeclared,
            _ => null
        };
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Deny(string reason)
    {
        AuthorizationLog.AccessDenied(logger, Declared.Operation, reason, currentUser.UserId, currentUser.TenantId, currentUser.Role?.ToString());
        throw new ForbiddenException(TenancyErrors.Forbidden);
    }

    private sealed record Declaration(string Operation, bool Anonymous, Permission[] Permissions, bool PlatformOperator)
    {
        public static Declaration Of(Type request) => new(
            request.Name,
            request.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true),
            [.. ((RequiresPermissionAttribute[])request.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true))
                .Select(attribute => attribute.Permission)],
            request.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true));
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

        // Una operación de plataforma ejecutada con un token que no es una sesión de plataforma (spec 011).
        public const string NotPlatformSession = "NotPlatformSession";

        // La jerarquía de roles (spec 009): tiene el permiso, pero no puede gestionar a ese rol.
        public const string RoleNotManageable = "RoleNotManageable";
        public const string CannotManageSelf = "CannotManageSelf";
    }

    // La IP la añade el scope de RequestLogScopeMiddleware.
    [LoggerMessage(EventId = SecurityEventIds.AccessDenied, Level = LogLevel.Warning,
        Message = "Acceso denegado a {Operation} ({Reason}). UserId {UserId}, TenantId {TenantId}, rol {Role}")]
    public static partial void AccessDenied(ILogger logger, string operation, string reason, Guid? userId, Guid? tenantId, string? role);

    [LoggerMessage(EventId = SecurityEventIds.TenantSuspendedAccessDenied, Level = LogLevel.Warning,
        Message = "Acceso denegado a {Operation}: la empresa está suspendida. UserId {UserId}, TenantId {TenantId}")]
    public static partial void TenantSuspended(ILogger logger, string operation, Guid? userId, Guid? tenantId);

    [LoggerMessage(EventId = SecurityEventIds.PlatformSessionRevoked, Level = LogLevel.Warning,
        Message = "Token de plataforma válido de una cuenta que ya no es un operador activo, en {Operation}. UserId {UserId}")]
    public static partial void PlatformSessionRevoked(ILogger logger, string operation, Guid? userId);

    [LoggerMessage(EventId = SecurityEventIds.MembershipRevoked, Level = LogLevel.Warning,
        Message = "Token válido sin pertenencia a la empresa en {Operation}. UserId {UserId}, TenantId {TenantId}")]
    public static partial void MembershipRevoked(ILogger logger, string operation, Guid? userId, Guid? tenantId);
}

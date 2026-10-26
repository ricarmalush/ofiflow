using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// Quien hace la petición, fijado por el test. <paramref name="role"/> es el rol <b>efectivo</b> (el de la base de datos
/// en producción); <paramref name="tokenRole"/>, lo que diría el token, que puede ser otro. Si <paramref name="role"/> es
/// nulo, la persona ya no pertenece a la empresa. Cuenta cuántas veces se verifica la pertenencia.
/// </summary>
public sealed class FakeCurrentUser(
    TenantRole? role,
    Guid? userId = null,
    Guid? tenantId = null,
    TenantRole? tokenRole = null,
    bool tenantSuspended = false,
    bool platformSession = false,
    bool platformOperatorActive = false) : ICurrentUser
{
    /// <summary>El token es una sesión de plataforma (spec 011 R7).</summary>
    public bool IsPlatformSession { get; } = platformSession;

    /// <summary>Cuántas veces se ha consultado si la cuenta sigue siendo un operador activo (en producción, una consulta a la base de datos cada vez).</summary>
    public int PlatformVerifyCalls { get; private set; }

    public Task<bool> VerifyPlatformOperatorAsync(CancellationToken cancellationToken)
    {
        PlatformVerifyCalls++;
        return Task.FromResult(IsPlatformSession && platformOperatorActive);
    }

    public Guid? UserId { get; } = userId;

    public Guid? TenantId { get; } = tenantId;

    public TenantRole? Role { get; } = role;

    /// <summary>La empresa está suspendida: la persona sigue siendo miembro, pero no hay rol efectivo (spec 011 R15d).</summary>
    public bool TenantSuspended { get; } = tenantSuspended;

    public TenantRole? TokenRole { get; } = tokenRole ?? role;

    /// <summary>Cuántas veces se ha consultado la pertenencia (en producción, una consulta a la base de datos cada vez).</summary>
    public int VerifyCalls { get; private set; }

    public Task<bool> VerifyMembershipAsync(CancellationToken cancellationToken)
    {
        VerifyCalls++;
        return Task.FromResult(Role is not null);
    }
}

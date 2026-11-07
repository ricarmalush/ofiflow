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
    bool platformOperatorActive = false,
    Guid? sessionId = null,
    bool secondFactorEnrollmentRequired = false) : ICurrentUser
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

    /// <summary>La sesión del token (el «sid», spec 015): la que la lista marca como actual (spec 016).</summary>
    public Guid? SessionId { get; } = sessionId;

    public TenantRole? Role { get; } = role;

    /// <summary>La empresa está suspendida: la persona sigue siendo miembro, pero no hay rol efectivo (spec 011 R15d).</summary>
    public bool TenantSuspended { get; } = tenantSuspended;

    /// <summary>El rol efectivo exige el segundo factor y la cuenta no lo tiene activo (spec 022). Solo vale tras verificar la pertenencia, como en producción.</summary>
    public bool SecondFactorEnrollmentRequired => VerifyCalls > 0 && Role is not null && secondFactorEnrollmentRequired;

    public TenantRole? TokenRole { get; } = tokenRole ?? role;

    /// <summary>Cuántas veces se ha consultado la pertenencia (en producción, una consulta a la base de datos cada vez).</summary>
    public int VerifyCalls { get; private set; }

    public Task<bool> VerifyMembershipAsync(CancellationToken cancellationToken)
    {
        VerifyCalls++;
        return Task.FromResult(Role is not null);
    }
}

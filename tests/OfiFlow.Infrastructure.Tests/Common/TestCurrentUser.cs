using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Tests.Common;

/// <summary>Quién hace la petición, fijado por el test (en producción sale del token y de la base de datos).</summary>
public sealed class TestCurrentUser(TenantRole? role, Guid? userId, Guid? tenantId) : ICurrentUser
{
    public Guid? UserId { get; } = userId;

    public Guid? TenantId { get; } = tenantId;

    public TenantRole? Role { get; } = role;

    public TenantRole? TokenRole { get; } = role;

    public Task<bool> VerifyMembershipAsync(CancellationToken cancellationToken) => Task.FromResult(Role is not null);
}

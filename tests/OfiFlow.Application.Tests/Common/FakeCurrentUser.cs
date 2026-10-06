using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Common;

public sealed class FakeCurrentUser(TenantRole? role, Guid? userId = null, Guid? tenantId = null) : ICurrentUser
{
    public Guid? UserId { get; } = userId;

    public Guid? TenantId { get; } = tenantId;

    public TenantRole? Role { get; } = role;
}

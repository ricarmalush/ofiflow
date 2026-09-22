using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Tests.Common;

public sealed class FakeTenantContext(Guid tenantId) : ITenantContext
{
    public Guid TenantId { get; } = tenantId;
}

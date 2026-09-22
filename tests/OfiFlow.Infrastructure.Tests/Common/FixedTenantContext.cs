using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Infrastructure.Tests.Common;

public sealed class FixedTenantContext(Guid tenantId) : ITenantContext
{
    public Guid TenantId { get; } = tenantId;
}

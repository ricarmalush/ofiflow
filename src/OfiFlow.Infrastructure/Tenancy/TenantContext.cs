using Microsoft.AspNetCore.Http;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;

namespace OfiFlow.Infrastructure.Tenancy;

/// <summary>
/// Resolves the active tenant from the tenant claim of the authenticated user's JWT —
/// never from a client-supplied header or query parameter (ADR-002).
/// </summary>
public sealed class TenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public Guid TenantId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User.FindFirst(OfiFlowClaimTypes.TenantId)
                ?? throw new InvalidOperationException("No hay un tenant activo en el contexto actual.");

            return Guid.Parse(claim.Value);
        }
    }
}

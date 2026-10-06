using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Quién hace la petición, leído del token ya validado (ADR-012 R4). Cada dato es nulo si falta o no
/// es válido, nunca una excepción: decidir qué hacer ante un dato ausente es cosa de quien lo usa
/// (el AuthorizationBehavior lo trata como "denegar").
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public Guid? UserId => ParseGuid(JwtRegisteredClaimNames.Sub);

    public Guid? TenantId => ParseGuid(OfiFlowClaimTypes.TenantId);

    public TenantRole? Role
    {
        get
        {
            var value = Principal?.FindFirst(OfiFlowClaimTypes.Role)?.Value;

            // Solo vale el nombre exacto de un rol: Enum.TryParse también acepta números ("1", y hasta "999" sin rol)
            // y otras capitalizaciones; un valor así nunca lo emite TokenService.
            return Enum.TryParse<TenantRole>(value, ignoreCase: false, out var role) && Enum.IsDefined(role) && role.ToString() == value
                ? role
                : null;
        }
    }

    private Guid? ParseGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirst(claimType)?.Value, out var id) ? id : null;
}

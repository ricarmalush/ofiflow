using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Quién hace la petición. La identidad se lee del token ya validado; el rol, de la base de datos en cada petición
/// (ADR-013, spec 009 R16), porque el del token puede estar desfasado. Cada dato es nulo si falta o no es válido,
/// nunca una excepción: decidir qué hacer ante un dato ausente es cosa de quien lo usa (el AuthorizationBehavior lo
/// trata como "denegar"). Es un servicio por petición: lo que verifica solo vale para esa petición.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor, ApplicationDbContext dbContext) : ICurrentUser
{
    private bool _verified;
    private TenantRole? _effectiveRole;

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public Guid? UserId => ParseGuid(JwtRegisteredClaimNames.Sub);

    public Guid? TenantId => ParseGuid(OfiFlowClaimTypes.TenantId);

    /// <summary>Fail-closed: hasta verificar la pertenencia, no hay ningún rol.</summary>
    public TenantRole? Role => _verified ? _effectiveRole : null;

    public TenantRole? TokenRole
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

    public async Task<bool> VerifyMembershipAsync(CancellationToken cancellationToken)
    {
        // Una sola consulta por petición: si ya se verificó, se reutiliza el resultado.
        if (_verified)
        {
            return _effectiveRole is not null;
        }

        var userId = UserId;
        var tenantId = TenantId;

        if (userId is null || tenantId is null)
        {
            // Un token sin la identidad completa: no se consulta nada (el filtro de empresa necesita el tenant) y se deniega.
            _effectiveRole = null;
            _verified = true;
            return false;
        }

        // La consulta usa solo lo que dice el token ya validado, nunca nada que mande el cliente. Va por el índice único
        // (TenantId, UserId). Se indica la empresa explícitamente además del filtro global, para que no dependa de él.
        _effectiveRole = await dbContext.TenantUsers
            .AsNoTracking()
            .Where(tu => tu.TenantId == tenantId.Value && tu.UserId == userId.Value)
            .Select(tu => (TenantRole?)tu.Role)
            .FirstOrDefaultAsync(cancellationToken);

        _verified = true;
        return _effectiveRole is not null;
    }

    private Guid? ParseGuid(string claimType) =>
        Guid.TryParse(Principal?.FindFirst(claimType)?.Value, out var id) ? id : null;
}

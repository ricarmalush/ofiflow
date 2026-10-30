using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// Desde la spec 015 la API solo acepta un access token de empresa si su sesión tiene un refresh token activo. Los tests que fabrican
/// un token a mano (para formas que el login nunca emite: otro rol, sin rol, una cuenta sembrada sin hacer login) necesitan, además
/// del token, una sesión de verdad. Se abre por el servicio real, no tocando la tabla (es interna), y se devuelve su Id para
/// ponerlo en el claim «sid» del token fabricado.
/// </summary>
internal static class TestSessions
{
    public static string OpenSessionId(SqlServerApiFactory factory, Guid userId, Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();

        // El rol con el que se emite este token auxiliar da igual: solo se usa su «sid».
        var issued = scope.ServiceProvider.GetRequiredService<ITokenService>()
            .IssueTokensAsync(userId, tenantId, TenantRole.Technician, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new JsonWebTokenHandler().ReadJsonWebToken(issued.AccessToken).GetClaim(OfiFlowClaimTypes.SessionId).Value;
    }
}

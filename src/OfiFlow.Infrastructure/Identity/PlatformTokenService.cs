using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Platform;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Firma la sesión de un operador de la plataforma (spec 011 R7). Es un servicio aparte del de las empresas a propósito: otra clave
/// (<c>Jwt:PlatformSecret</c>), otra audiencia, 15 minutos y <b>ni rastro de refresh tokens</b>. No toca la base de datos y no comprueba
/// quién es la cuenta: solo el canje del desafío de plataforma lo llama, después de comprobarlo todo.
/// </summary>
public sealed class PlatformTokenService(IOptions<JwtOptions> jwtOptions, TimeProvider? timeProvider = null) : IPlatformTokenService
{
    private readonly JwtOptions _options = jwtOptions.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public PlatformSessionDto IssuePlatformSession(Guid operatorUserId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expiresAt = now + PlatformAuthentication.SessionLifetime;

        // Solo la cuenta y el mundo: ni empresa ni rol (un operador no tiene ninguno), y el claim de mundo es lo que la política de
        // las rutas de plataforma exige además de la firma. El Jti hace único cada token.
        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, operatorUserId.ToString()),
            new Claim(OfiFlowClaimTypes.Realm, OfiFlowClaimTypes.PlatformRealm),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        ];

        var key = new SymmetricSecurityKey(Convert.FromBase64String(_options.PlatformSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.PlatformAudience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        return new PlatformSessionDto(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

using OfiFlow.Application.Platform;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Emite la sesión de un operador de la plataforma (spec 011 R7, ADR-016). Es un servicio aparte de <see cref="ITokenService"/>
/// a propósito: otra clave, otra audiencia y ni rastro de refresh tokens, para que el mundo de la plataforma y el de las
/// empresas no puedan confundirse. Solo lo llama el canje del desafío de plataforma (R8; lo fija un test de arquitectura).
/// </summary>
public interface IPlatformTokenService
{
    /// <summary>
    /// Firma un token de 15 minutos para la cuenta, con la clave y la audiencia de plataforma. No toca la base de datos y no
    /// comprueba nada: quien llama ya comprobó que es un operador activo y que superó el segundo factor.
    /// </summary>
    PlatformSessionDto IssuePlatformSession(Guid operatorUserId);
}

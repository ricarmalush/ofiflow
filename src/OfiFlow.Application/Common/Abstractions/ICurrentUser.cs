using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Quién hace la petición. La identidad (<see cref="UserId"/>, <see cref="TenantId"/>) sale del token ya validado;
/// el <b>rol</b>, en cambio, sale de la base de datos y se comprueba en cada petición (ADR-013, spec 009 R16): el del
/// token puede estar desfasado (se cambió el rol, o se quitó a la persona, después de emitirlo) y por eso no autoriza nada.
/// Cada dato es nulo si falta o no es válido.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    Guid? TenantId { get; }

    /// <summary>
    /// El rol <b>efectivo</b>: el de la pertenencia en la base de datos. Es nulo hasta que se llama a
    /// <see cref="VerifyMembershipAsync"/> (lo hace el AuthorizationBehavior antes que nada) y también si la persona ya no
    /// pertenece a la empresa. Fail-closed: quien lo lea sin verificar no ve ningún rol.
    /// </summary>
    TenantRole? Role { get; }

    /// <summary>
    /// Verdadero si la persona <b>sigue</b> perteneciendo a la empresa, pero la empresa está <b>suspendida</b> (spec 011 R15d).
    /// Solo vale después de <see cref="VerifyMembershipAsync"/>; antes es falso. En ese caso <see cref="Role"/> es nulo (no hay
    /// rol efectivo en una empresa suspendida) y la verificación devuelve falso: se distingue de «ya no pertenece» para poder
    /// avisar con el código correcto y dejar el evento correcto.
    /// </summary>
    bool TenantSuspended { get; }

    /// <summary>El rol que dice el token. Es solo informativo: <b>no autoriza nada</b> y puede estar desfasado.</summary>
    TenantRole? TokenRole { get; }

    /// <summary>
    /// Comprueba en la base de datos que la persona sigue perteneciendo a la empresa del token y fija su rol efectivo
    /// (<see cref="Role"/>). Es una única consulta por el índice único (empresa, usuario), que no depende de nada que mande
    /// el cliente, y se hace una sola vez por petición. Devuelve falso si ya no pertenece, o si el token no trae
    /// la identidad completa.
    /// </summary>
    Task<bool> VerifyMembershipAsync(CancellationToken cancellationToken);

    /// <summary>
    /// El token de esta petición es una <b>sesión de plataforma</b> (lleva el claim del mundo de plataforma, spec 011 R7). Solo
    /// dice qué tipo de sesión es; que la persona <b>siga siendo</b> un operador lo dice <see cref="VerifyPlatformOperatorAsync"/>.
    /// Una sesión de empresa nunca lo es: no puede firmar ese claim, porque lo firma otra clave.
    /// </summary>
    bool IsPlatformSession { get; }

    /// <summary>
    /// Comprueba en la base de datos que la cuenta del token de plataforma <b>sigue siendo un operador no dado de baja</b>
    /// (spec 011 R10): un token todavía válido de un operador dado de baja no sirve. Se hace una sola vez por petición.
    /// Falso si el token no es de plataforma, no trae la cuenta o la cuenta ya no es un operador activo.
    /// </summary>
    Task<bool> VerifyPlatformOperatorAsync(CancellationToken cancellationToken);
}

namespace OfiFlow.Application.Platform;

/// <summary>
/// La sesión de un operador de la plataforma (spec 011 R7). A diferencia de la de una empresa, <b>no trae refresh token</b>:
/// pasados los 15 minutos hay que repetir la contraseña y el código.
/// </summary>
public sealed record PlatformSessionDto(string AccessToken, DateTime ExpiresAt);

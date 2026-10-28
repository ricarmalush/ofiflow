using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.Logout;

/// <summary>
/// Cierra la sesión de un refresh token (spec 013). Anónimo a propósito: la credencial es el propio refresh token y cerrar sesión
/// tiene que poder hacerse cuando el access token ya caducó.
/// </summary>
[AllowAnonymousRequest]
public sealed record LogoutCommand(string RefreshToken) : IRequest<Unit>;

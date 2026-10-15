using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.AcceptInvitation;

/// <summary>
/// La persona invitada acepta con el token que le entregaron y elige su nombre y su contraseña. Es anónima
/// (todavía no tiene cuenta): el token es la credencial. El email y el rol los fijó quien invitó; esta
/// operación no permite elegir otros. No devuelve tokens de sesión: la persona entra con el login de siempre.
/// </summary>
[AllowAnonymousRequest]
public sealed record AcceptInvitationCommand(string Token, string Name, string Password) : IRequest;

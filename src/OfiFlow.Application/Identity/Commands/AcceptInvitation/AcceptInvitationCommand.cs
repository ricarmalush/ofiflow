using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.AcceptInvitation;

/// <summary>
/// La persona invitada acepta con el token que le entregaron y elige su nombre y su contraseña. Es anónima
/// (todavía no tiene cuenta): el token es la credencial. El email y el rol los fijó quien invitó; esta
/// operación no permite elegir otros. No devuelve tokens de sesión: la persona entra con el login de siempre.
/// También acepta las condiciones de uso vigentes (spec 021 D8, R16): toda persona que crea una cuenta las acepta, no solo quien registra la empresa.
/// </summary>
/// <param name="AcceptedTermsVersion">La versión de las condiciones que la persona vio y aceptó. Debe ser la vigente (<c>Legal:TermsVersion</c>); se guarda en su <c>User</c>.</param>
[AllowAnonymousRequest]
public sealed record AcceptInvitationCommand(string Token, string Name, string Password, string AcceptedTermsVersion) : IRequest;

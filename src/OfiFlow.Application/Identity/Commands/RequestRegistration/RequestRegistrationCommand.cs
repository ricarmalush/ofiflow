using MediatR;
using OfiFlow.Application.Common.Authorization;

namespace OfiFlow.Application.Identity.Commands.RequestRegistration;

/// <summary>
/// Pedir crear una cuenta (spec 021). Es anónima (quien se registra aún no tiene cuenta) y <b>responde siempre lo mismo</b>: el email es nuevo, ya tiene cuenta (de una empresa o de un operador de la
/// plataforma), se pasó el límite, el correo está apagado o las condiciones no están configuradas (R1). <b>No crea ninguna cuenta</b>: a lo sumo deja un pendiente y encola un correo con un enlace;
/// la cuenta nace cuando se abre ese enlace (D1). No devuelve nada, y menos el token. <b>No lleva contraseña</b>: se elige al completar (D5).
/// </summary>
/// <remarks>
/// Es un <c>record</c> y por tanto su <c>ToString()</c> imprimiría todos los campos: el email y los nombres son datos personales y podrían acabar en un registro de errores. Se sustituye por uno que
/// no imprime nada de lo que escribió la persona (ADR-009 R5).
/// </remarks>
[AllowAnonymousRequest]
public sealed record RequestRegistrationCommand(string CompanyName, string UserName, string Email, string AcceptedTermsVersion) : IRequest
{
    public override string ToString() => nameof(RequestRegistrationCommand);
}

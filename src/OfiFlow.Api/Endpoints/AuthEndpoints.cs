using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Options;
using OfiFlow.Api.Common;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.AcceptInvitation;
using OfiFlow.Application.Identity.Commands.CompleteRegistration;
using OfiFlow.Application.Identity.Commands.ForgotPassword;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Identity.Commands.Logout;
using OfiFlow.Application.Identity.Commands.RefreshToken;
using OfiFlow.Application.Identity.Commands.RequestRegistration;
using OfiFlow.Application.Identity.Commands.ResetPassword;
using OfiFlow.Application.Identity.Commands.VerifyMfaChallenge;

namespace OfiFlow.Api.Endpoints;

public static class AuthEndpoints
{
    public const string Route = "/api/v1/auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Auth").AllowAnonymous();

        // Spec 021: pedir crear una cuenta. Anónima, y la respuesta es SIEMPRE la misma (202 sin cuerpo): el email es nuevo, ya tiene cuenta (de una empresa o de un operador), se pasó el límite del
        // email, el correo está apagado o las condiciones no están configuradas. No crea ninguna cuenta: deja un pendiente y un correo con un enlace; la cuenta nace al abrirlo (bloque 3).
        // Solo un dato mal formado o la versión de las condiciones equivocada dan 400, y eso se decide sin consultar nada.
        group.MapPost("/registration", async (RequestRegistrationCommand command, ISender sender, IOptions<RegistrationTimingOptions> timing, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                await sender.Send(command, cancellationToken);

                // El suelo de tiempo (R2, D2): con un email nuevo la petición hace más trabajo (buscar, contar, anular, insertar, confirmar) que con uno que ya tiene cuenta (solo buscar), y esa diferencia
                // delataría al email aunque la respuesta sea idéntica. Se aplica DESPUÉS del trabajo. Un dato mal formado no llega aquí (la validación lanza antes) y no hace falta: se decide sin consultar nada.
                await ResponseTimeFloor.HoldAsync(started, timing.Value.ResponseFloor, cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Accepted();
            })
            .WithName("RequestRegistration")
            .WithSummary("Pide crear una cuenta (empresa y primer usuario). Responde 202 sin cuerpo siempre (sea el email nuevo o ya tenga cuenta); no crea ninguna cuenta: envía por correo un enlace de un solo uso que caduca a las 24 horas, y la cuenta nace al abrirlo. 400 si falta algún dato o la versión de las condiciones no es la vigente.")
            .RequireRateLimiting(RateLimiting.Registration);

        // Spec 021: completar el registro con el token del correo. Anónima: la credencial es el token. AQUÍ nace la cuenta (empresa, usuario y Owner, todo o nada). Un token que no sirve
        // (desconocido, caducado, usado, sustituido o con el email ya registrado) da SIEMPRE el mismo 400 registration.invalid. No inicia sesión: se entra con el login de siempre.
        group.MapPost("/registration/complete", async (CompleteRegistrationCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.NoContent();
            })
            .WithName("CompleteRegistration")
            .WithSummary("Completa el registro con el token del enlace del correo y la contraseña elegida: crea la empresa, el usuario y su pertenencia como Owner. 204 si sirvió; 400 registration.invalid, siempre el mismo, si el token no sirve. No inicia sesión.")
            .RequireRateLimiting(RateLimiting.RegistrationComplete);

        group.MapPost("/login", async (LoginCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(command, cancellationToken);

                switch (result)
                {
                    case LoginResult.Authenticated authenticated:
                        return Results.Ok(authenticated.Tokens);

                    case LoginResult.SecondFactorRequired required:
                        // Falta el segundo factor (spec 010 R10): no hay sesión, hay un desafío. Contiene un valor secreto que no
                        // se vuelve a mostrar, así que ninguna caché debe guardarlo (igual que el token de una invitación).
                        httpContext.Response.Headers.CacheControl = "no-store";
                        return Results.Ok(new { mfaRequired = true, challenge = required.Challenge.Challenge, expiresAt = required.Challenge.ExpiresAt });

                    default:
                        return Results.Unauthorized();
                }
            })
            .WithName("Login")
            .WithSummary("Autentica con email y contraseña. Sin segundo factor devuelve Access Token + Refresh Token; con él devuelve 200 { mfaRequired, challenge, expiresAt } (sin sesión) y hay que canjear el desafío en /auth/mfa/verify.")
            .RequireRateLimiting(RateLimiting.Login);

        group.MapPost("/mfa/verify", async (VerifyMfaChallengeCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(command, cancellationToken);
                return result is null ? Results.Unauthorized() : Results.Ok(result);
            })
            .WithName("VerifyMfaChallenge")
            .WithSummary("Segundo paso del login: canjea el desafío con un código de la app o de recuperación y devuelve la sesión. Todos los fallos dan el mismo 401.")
            .RequireRateLimiting(RateLimiting.MfaVerify);

        group.MapPost("/refresh", async (RefreshTokenCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(command, cancellationToken);
                return result is null ? Results.Unauthorized() : Results.Ok(result);
            })
            .WithName("Refresh")
            .WithSummary("Rota un Refresh Token válido por un nuevo par de tokens.")
            .RequireRateLimiting(RateLimiting.Refresh);

        group.MapPost("/logout", async (LogoutCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);

                // 204 siempre que la forma sea válida, exista el token o no (spec 013 R4): la ruta no debe servir para comprobar tokens.
                return Results.NoContent();
            })
            .WithName("Logout")
            .WithSummary("Cierra la sesión del Refresh Token recibido (revocándolo). Anónimo: no pide access token, para poder salir con la sesión caducada. Responde 204 exista el token o no. Desde la spec 015 el access token de esa sesión también deja de valer al instante.")
            .RequireRateLimiting(RateLimiting.Logout);

        group.MapPost("/accept-invitation", async (AcceptInvitationCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);

                // Sin tokens de sesión: la persona entra con el login de siempre, con la contraseña que acaba de elegir.
                return Results.NoContent();
            })
            .WithName("AcceptInvitation")
            .WithSummary("Acepta una invitación con su token: la persona elige su nombre y su contraseña y se crea su cuenta.")
            .RequireRateLimiting(RateLimiting.AcceptInvitation);

        // Spec 018: «he olvidado mi contraseña». Anónima, y la respuesta es SIEMPRE la misma (202 sin cuerpo): exista o no la cuenta, esté bloqueada, suspendida o del operador, esté el
        // correo apagado o se haya pasado el límite de la cuenta. Solo un email mal formado da 400, y eso se decide sin consultar nada.
        group.MapPost("/forgot-password", async (ForgotPasswordCommand command, ISender sender, IOptions<ForgotPasswordTimingOptions> timing, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                await sender.Send(command, cancellationToken);

                // El suelo de tiempo (R2, D2): con una cuenta la petición hace más trabajo que sin ella, y esa diferencia (medida: ~12 ms, inequívoca) la delataría aunque la respuesta sea idéntica.
                // Un email mal formado no llega aquí (la validación lanza antes) y no hace falta: se decide sin consultar nada.
                await ResponseTimeFloor.HoldAsync(started, timing.Value.ResponseFloor, cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.Accepted();
            })
            .WithName("ForgotPassword")
            .WithSummary("Pide restablecer la contraseña olvidada. Responde 202 sin cuerpo siempre (exista o no la cuenta); si existe, envía por correo un enlace de un solo uso que caduca a la hora.")
            .RequireRateLimiting(RateLimiting.ForgotPassword);

        // Spec 018: elegir la contraseña nueva con el token del correo. Anónima: la credencial es el token. Un token que no sirve (desconocido, caducado, usado o anulado) da SIEMPRE el mismo
        // 400 password_reset.invalid; si sirve, cambia la contraseña, cierra todas las sesiones, quita el bloqueo y avisa por correo, todo o nada. No inicia sesión: se entra con el login de
        // siempre (y con el segundo factor, si lo tiene).
        group.MapPost("/reset-password", async (ResetPasswordCommand command, ISender sender, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";

                return Results.NoContent();
            })
            .WithName("ResetPassword")
            .WithSummary("Restablece la contraseña con el token del enlace del correo. 204 si sirvió; 400 password_reset.invalid, siempre el mismo, si el token no sirve. Cierra todas las sesiones de la cuenta y no toca el segundo factor.")
            .RequireRateLimiting(RateLimiting.ResetPassword);
    }
}

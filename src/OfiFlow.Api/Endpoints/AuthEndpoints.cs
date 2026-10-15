using MediatR;
using OfiFlow.Api.Common;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.AcceptInvitation;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Identity.Commands.RefreshToken;
using OfiFlow.Application.Identity.Commands.Register;

namespace OfiFlow.Api.Endpoints;

public static class AuthEndpoints
{
    public const string Route = "/api/v1/auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).WithTags("Auth").AllowAnonymous();

        group.MapPost("/register", async (RegisterCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var tenantId = await sender.Send(command, cancellationToken);
                return Results.Ok(new { tenantId });
            })
            .WithName("Register")
            .WithSummary("Registra una nueva empresa (Tenant) y su primer usuario (Owner).")
            .RequireRateLimiting(RateLimiting.Register);

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
            .WithSummary("Autentica al usuario y emite Access Token + Refresh Token.")
            .RequireRateLimiting(RateLimiting.Login);

        group.MapPost("/refresh", async (RefreshTokenCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(command, cancellationToken);
                return result is null ? Results.Unauthorized() : Results.Ok(result);
            })
            .WithName("Refresh")
            .WithSummary("Rota un Refresh Token válido por un nuevo par de tokens.")
            .RequireRateLimiting(RateLimiting.Refresh);

        group.MapPost("/accept-invitation", async (AcceptInvitationCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                await sender.Send(command, cancellationToken);

                // Sin tokens de sesión: la persona entra con el login de siempre, con la contraseña que acaba de elegir.
                return Results.NoContent();
            })
            .WithName("AcceptInvitation")
            .WithSummary("Acepta una invitación con su token: la persona elige su nombre y su contraseña y se crea su cuenta.")
            .RequireRateLimiting(RateLimiting.AcceptInvitation);
    }
}

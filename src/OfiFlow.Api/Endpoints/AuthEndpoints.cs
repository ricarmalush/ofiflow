using MediatR;
using OfiFlow.Api.Common;
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

        group.MapPost("/login", async (LoginCommand command, ISender sender, CancellationToken cancellationToken) =>
            {
                var result = await sender.Send(command, cancellationToken);
                return result is null ? Results.Unauthorized() : Results.Ok(result);
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
    }
}

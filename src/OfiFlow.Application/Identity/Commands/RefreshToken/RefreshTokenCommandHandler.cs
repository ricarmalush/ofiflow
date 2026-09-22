using MediatR;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Identity.Commands.RefreshToken;

public sealed class RefreshTokenCommandHandler(ITokenService tokenService) : IRequestHandler<RefreshTokenCommand, AuthResultDto?>
{
    public Task<AuthResultDto?> Handle(RefreshTokenCommand request, CancellationToken cancellationToken) =>
        tokenService.RotateRefreshTokenAsync(request.RefreshToken, cancellationToken);
}

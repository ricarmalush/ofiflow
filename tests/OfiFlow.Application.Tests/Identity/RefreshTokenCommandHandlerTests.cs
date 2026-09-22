using OfiFlow.Application.Identity.Commands.RefreshToken;
using OfiFlow.Application.Tests.Common;

namespace OfiFlow.Application.Tests.Identity;

public class RefreshTokenCommandHandlerTests
{
    [Fact]
    public async Task Handle_WithValidToken_ReturnsNewTokens()
    {
        var handler = new RefreshTokenCommandHandler(new FakeTokenService());

        var result = await handler.Handle(new RefreshTokenCommand(FakeTokenService.ValidRefreshToken), CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Handle_WithInvalidToken_ReturnsNull()
    {
        var handler = new RefreshTokenCommandHandler(new FakeTokenService());

        var result = await handler.Handle(new RefreshTokenCommand("token-invalido"), CancellationToken.None);

        Assert.Null(result);
    }
}

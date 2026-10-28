using MediatR;
using OfiFlow.Application.Identity.Commands.Logout;
using OfiFlow.Application.Tests.Common;

namespace OfiFlow.Application.Tests.Identity;

public class LogoutCommandHandlerTests
{
    [Fact]
    public async Task Handle_PassesTheTokenToTheService_AndReturnsNothing()
    {
        var tokens = new FakeTokenService();
        var handler = new LogoutCommandHandler(tokens);

        var result = await handler.Handle(new LogoutCommand(FakeTokenService.ValidRefreshToken), CancellationToken.None);

        Assert.Equal(Unit.Value, result);
        Assert.Equal([FakeTokenService.ValidRefreshToken], tokens.RevokedRefreshTokens);
    }

    [Fact]
    public async Task Handle_WithATokenThatClosesNothing_ReturnsTheSameAsWithARealOne()
    {
        // R4: quien cierra con un token que no existe no debe poder distinguirlo de quien cierra uno real.
        var handler = new LogoutCommandHandler(new FakeTokenService());

        var real = await handler.Handle(new LogoutCommand(FakeTokenService.ValidRefreshToken), CancellationToken.None);
        var invented = await handler.Handle(new LogoutCommand("token-inventado"), CancellationToken.None);

        Assert.Equal(real, invented);
    }
}

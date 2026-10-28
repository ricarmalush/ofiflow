using MediatR;
using OfiFlow.Application.Identity.Commands.CloseAllSessions;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

public class CloseAllSessionsCommandHandlerTests
{
    [Fact]
    public async Task Handle_ClosesTheSessionsOfTheAccountInTheToken_AndReturnsNothing()
    {
        var userId = Guid.NewGuid();
        var tokens = new FakeTokenService();
        var handler = new CloseAllSessionsCommandHandler(tokens, new FakeCurrentUser(TenantRole.Technician, userId, Guid.NewGuid()));

        var result = await handler.Handle(new CloseAllSessionsCommand(), CancellationToken.None);

        Assert.Equal(Unit.Value, result);
        Assert.Equal([userId], tokens.UsersWithAllSessionsRevoked);
    }

    [Fact]
    public void TheCommand_HasNoPropertiesToForge_TheAccountComesFromTheToken()
    {
        // R1: sin parámetros no hay un Id con el que pedir las sesiones de otra persona. Si alguien añade uno, este test lo señala.
        Assert.Empty(typeof(CloseAllSessionsCommand).GetProperties()
            .Where(property => property.DeclaringType == typeof(CloseAllSessionsCommand) && property.Name != "EqualityContract"));
    }
}

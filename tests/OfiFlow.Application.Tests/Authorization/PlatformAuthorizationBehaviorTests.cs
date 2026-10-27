using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Authorization;

/// <summary>
/// El tercer tipo de declaración, <c>[RequiresPlatformOperator]</c> (spec 011 R10 y R14, ADR-016): solo la ejecuta una sesión de
/// plataforma de un operador que sigue activo, excluye a los otros dos tipos y, como siempre, ante la duda deniega.
/// </summary>
public class PlatformAuthorizationBehaviorTests
{
    [RequiresPlatformOperator]
    private sealed record ForOperators : IRequest<string>;

    [AllowAnonymousRequest]
    [RequiresPlatformOperator]
    private sealed record AnonymousAndPlatform : IRequest<string>;

    [RequiresPermission(Permission.JobsRead)]
    [RequiresPlatformOperator]
    private sealed record PermissionAndPlatform : IRequest<string>;

    [RequiresPermission(Permission.JobsRead)]
    private sealed record ForACompanyRole : IRequest<string>;

    private sealed record Outcome<TRequest>(string? Result, ForbiddenException? Denied, bool NextCalled, ListLogger<AuthorizationBehavior<TRequest, string>> Log)
        where TRequest : notnull;

    private static async Task<Outcome<TRequest>> Run<TRequest>(TRequest request, FakeCurrentUser user)
        where TRequest : notnull
    {
        var log = new ListLogger<AuthorizationBehavior<TRequest, string>>();
        var behavior = new AuthorizationBehavior<TRequest, string>(user, log);
        var nextCalled = false;

        try
        {
            var result = await behavior.Handle(request, _ =>
            {
                nextCalled = true;
                return Task.FromResult("ejecutada");
            }, CancellationToken.None);

            return new Outcome<TRequest>(result, null, nextCalled, log);
        }
        catch (ForbiddenException denied)
        {
            return new Outcome<TRequest>(null, denied, nextCalled, log);
        }
    }

    private static FakeCurrentUser ActiveOperator() =>
        new(role: null, userId: Guid.NewGuid(), platformSession: true, platformOperatorActive: true);

    [Fact]
    public async Task AnActiveOperatorWithAPlatformSession_ExecutesTheOperation()
    {
        var user = ActiveOperator();

        var outcome = await Run(new ForOperators(), user);

        Assert.Equal("ejecutada", outcome.Result);
        Assert.True(outcome.NextCalled);
        Assert.Empty(outcome.Log.Entries);
        Assert.Equal(1, user.PlatformVerifyCalls); // R10: se comprueba en la base de datos en CADA petición
    }

    [Fact]
    public async Task APlatformOperation_NeverLooksAtTheMembershipOfACompany()
    {
        // Un operador no pertenece a ninguna empresa: la pertenencia no tiene nada que decir.
        var user = ActiveOperator();

        await Run(new ForOperators(), user);

        Assert.Equal(0, user.VerifyCalls);
    }

    [Fact]
    public async Task ADisabledOperatorWithAStillValidToken_IsDeniedOnTheSameRequest_AndLeavesTheEvent()
    {
        // R10: el token no ha caducado, pero la cuenta ya no es un operador activo. Evento 1608.
        var userId = Guid.NewGuid();
        var user = new FakeCurrentUser(role: null, userId: userId, platformSession: true, platformOperatorActive: false);

        var outcome = await Run(new ForOperators(), user);

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);

        var logged = Assert.Single(outcome.Log.Entries);
        Assert.Equal(SecurityEventIds.PlatformSessionRevoked, logged.EventId.Id);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Contains(userId.ToString(), logged.Message);
    }

    [Fact]
    public async Task ATokenThatIsNotAPlatformSession_IsDenied_EvenIfTheAccountHappenedToBeAnOperator()
    {
        // Defensa en profundidad: una sesión de empresa (sin el claim del mundo de plataforma) nunca ejecuta una operación de
        // plataforma, y ni siquiera se llega a consultar si la cuenta es un operador.
        var user = new FakeCurrentUser(TenantRole.Owner, userId: Guid.NewGuid(), tenantId: Guid.NewGuid(), platformSession: false, platformOperatorActive: true);

        var outcome = await Run(new ForOperators(), user);

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Equal(0, user.PlatformVerifyCalls);

        var logged = Assert.Single(outcome.Log.Entries);
        Assert.Equal(SecurityEventIds.AccessDenied, logged.EventId.Id);
        Assert.Contains("NotPlatformSession", logged.Message);
    }

    [Fact]
    public async Task AnOperationDeclaredAsAnonymousAndPlatform_IsDenied_NotLetThroughAsAnonymous()
    {
        // El atajo de las operaciones anónimas no puede dejar pasar sin control una que además es de plataforma.
        var user = ActiveOperator();

        var outcome = await Run(new AnonymousAndPlatform(), user);

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Contains("Misdeclared", Assert.Single(outcome.Log.Entries).Message);
        Assert.Equal(0, user.PlatformVerifyCalls); // mal declarada: se deniega antes de consultar nada
    }

    [Fact]
    public async Task AnOperationDeclaredWithAPermissionAndPlatform_IsDenied()
    {
        var outcome = await Run(new PermissionAndPlatform(), ActiveOperator());

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Contains("Misdeclared", Assert.Single(outcome.Log.Entries).Message);
    }

    [Fact]
    public async Task AnActiveOperator_CannotRunACompanyOperation()
    {
        // El operador no ve ni un cliente ni un trabajo de nadie: una operación de empresa exige pertenencia, y no la tiene.
        var user = ActiveOperator();

        var outcome = await Run(new ForACompanyRole(), user);

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Equal(0, user.PlatformVerifyCalls);
    }
}

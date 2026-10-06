using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Authorization;

/// <summary>ADR-012 R3: el AuthorizationBehavior deniega lo que no está permitido y lo que no está declarado.</summary>
public class AuthorizationBehaviorTests
{
    // Operaciones de prueba, declaradas de cada manera posible.
    [RequiresPermission(Permission.JobsAssign)]
    private sealed record NeedsAssign : IRequest<string>;

    [RequiresPermission(Permission.JobsRead)]
    [RequiresPermission(Permission.CustomersDelete)]
    private sealed record NeedsReadAndDelete : IRequest<string>;

    [AllowAnonymousRequest]
    private sealed record Anonymous : IRequest<string>;

    private sealed record Undeclared : IRequest<string>;

    [AllowAnonymousRequest]
    [RequiresPermission(Permission.JobsRead)]
    private sealed record Contradictory : IRequest<string>;

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

    [Fact]
    public async Task ARoleWithThePermission_ExecutesTheOperation()
    {
        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser(TenantRole.Manager));

        Assert.Equal("ejecutada", outcome.Result);
        Assert.True(outcome.NextCalled);
        Assert.Empty(outcome.Log.Entries);
    }

    [Fact]
    public async Task ARoleWithoutThePermission_IsDeniedAndTheHandlerNeverRuns()
    {
        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser(TenantRole.Employee));

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
    }

    [Fact]
    public async Task WhenSeveralPermissionsAreRequired_HavingOnlyOneIsNotEnough()
    {
        // Manager tiene Jobs.Read pero no Customers.Delete.
        var outcome = await Run(new NeedsReadAndDelete(), new FakeCurrentUser(TenantRole.Manager));

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
    }

    [Fact]
    public async Task WhenSeveralPermissionsAreRequired_HavingAllOfThemIsEnough()
    {
        var outcome = await Run(new NeedsReadAndDelete(), new FakeCurrentUser(TenantRole.Admin));

        Assert.Equal("ejecutada", outcome.Result);
        Assert.True(outcome.NextCalled);
    }

    [Fact]
    public async Task AnOperationWithoutAnyDeclaration_IsDenied_EvenForTheOwner()
    {
        // Fail-closed (ADR-012 R3): olvidar declarar una operación nunca la deja abierta, ni siquiera al Owner.
        var outcome = await Run(new Undeclared(), new FakeCurrentUser(TenantRole.Owner));

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Contains("NotDeclared", outcome.Log.Entries.Single().Message);
    }

    [Fact]
    public async Task AnOperationDeclaredAsAnonymousAndWithAPermission_IsDenied()
    {
        var outcome = await Run(new Contradictory(), new FakeCurrentUser(TenantRole.Owner));

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Contains("Misdeclared", outcome.Log.Entries.Single().Message);
    }

    [Fact]
    public async Task AnAnonymousOperation_ExecutesWithoutAnyUser()
    {
        var outcome = await Run(new Anonymous(), new FakeCurrentUser(role: null));

        Assert.Equal("ejecutada", outcome.Result);
        Assert.True(outcome.NextCalled);
        Assert.Empty(outcome.Log.Entries);
    }

    [Fact]
    public async Task AnAuthenticatedOperationWithoutARole_IsDenied()
    {
        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser(role: null));

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Contains("NoValidRole", outcome.Log.Entries.Single().Message);
    }

    [Fact]
    public async Task ARoleOutsideTheEnum_IsDenied()
    {
        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser((TenantRole)999));

        Assert.NotNull(outcome.Denied);
        Assert.False(outcome.NextCalled);
    }

    [Fact]
    public async Task EveryDenial_IsLoggedAsASecurityEvent_WithIdsAndRoleButNoRequestData()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser(TenantRole.Technician, userId, tenantId));

        var entry = outcome.Log.Entries.Single();
        Assert.Equal(SecurityEventIds.AccessDenied, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(NeedsAssign), entry.Message);
        Assert.Contains("MissingPermission", entry.Message);
        Assert.Contains(userId.ToString(), entry.Message);
        Assert.Contains(tenantId.ToString(), entry.Message);
        Assert.Contains("Technician", entry.Message);
    }

    [Fact]
    public async Task ADenialWithNoUserData_StillLogsWithoutFailing()
    {
        // Un token sin sub ni tenant (ICurrentUser devuelve nulos) no puede romper el registro de la denegación.
        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser(role: null));

        Assert.Single(outcome.Log.Entries);
    }
}

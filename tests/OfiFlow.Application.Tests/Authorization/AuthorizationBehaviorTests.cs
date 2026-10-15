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

    // ===== Revalidación de la pertenencia en cada petición (spec 009 R16) =====

    [Fact]
    public async Task APersonWhoNoLongerBelongsToTheTenant_IsDenied_WithTheMembershipRevokedEvent()
    {
        // Su token sigue vivo, pero la base de datos dice que ya no es de la empresa (role: null = sin pertenencia).
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser(role: null, userId, tenantId, tokenRole: TenantRole.Owner);

        var outcome = await Run(new NeedsAssign(), user);

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        var entry = outcome.Log.Entries.Single();
        Assert.Equal(SecurityEventIds.MembershipRevoked, entry.EventId.Id); // 1302, no el 1301 de "sin permiso"
        Assert.Contains(userId.ToString(), entry.Message);
        Assert.Contains(tenantId.ToString(), entry.Message);
        Assert.Contains(nameof(NeedsAssign), entry.Message);
    }

    [Fact]
    public async Task TheRoleThatDecides_IsTheOneInTheDatabase_NotTheOneInTheToken()
    {
        // El token dice Owner (y lo tuvo), pero hoy en la base de datos es Technician: no puede asignar trabajos.
        var user = new FakeCurrentUser(TenantRole.Technician, Guid.NewGuid(), Guid.NewGuid(), tokenRole: TenantRole.Owner);

        var outcome = await Run(new NeedsAssign(), user);

        Assert.NotNull(outcome.Denied);
        Assert.False(outcome.NextCalled);
        Assert.Contains("MissingPermission", outcome.Log.Entries.Single().Message);
    }

    [Fact]
    public async Task AToken_ThatUnderstatesTheRole_DoesNotLimitWhatTheDatabaseAllows()
    {
        // Al revés: el token dice Technician, pero hoy es Manager (lo ascendieron): ya puede, sin renovar la sesión.
        var user = new FakeCurrentUser(TenantRole.Manager, Guid.NewGuid(), Guid.NewGuid(), tokenRole: TenantRole.Technician);

        var outcome = await Run(new NeedsAssign(), user);

        Assert.Equal("ejecutada", outcome.Result);
        Assert.Null(outcome.Denied);
    }

    [Fact]
    public async Task TheMembership_IsVerifiedExactlyOnce_BeforeTheOperationRuns()
    {
        var user = new FakeCurrentUser(TenantRole.Manager, Guid.NewGuid(), Guid.NewGuid());

        await Run(new NeedsAssign(), user);

        Assert.Equal(1, user.VerifyCalls);
    }

    [Fact]
    public async Task AnAnonymousOperation_NeverVerifiesAnything()
    {
        var user = new FakeCurrentUser(role: null);

        await Run(new Anonymous(), user);

        Assert.Equal(0, user.VerifyCalls);
    }

    [Fact]
    public async Task AnUndeclaredOperation_IsDeniedWithoutSpendingAQuery()
    {
        // Un error de quien escribió la operación se deniega antes de consultar nada.
        var user = new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid(), Guid.NewGuid());

        var outcome = await Run(new Undeclared(), user);

        Assert.NotNull(outcome.Denied);
        Assert.Equal(0, user.VerifyCalls);
    }

    [Fact]
    public async Task AContradictoryOperation_IsDeniedWithoutSpendingAQuery()
    {
        var user = new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid(), Guid.NewGuid());

        var outcome = await Run(new Contradictory(), user);

        Assert.NotNull(outcome.Denied);
        Assert.Equal(0, user.VerifyCalls);
    }

    [Fact]
    public async Task ADeniedPermission_DoesNotMentionTheRemovedMembershipEvent()
    {
        // Control: quien sigue en la empresa pero no tiene el permiso recibe el 1301 de siempre, no el 1302.
        var outcome = await Run(new NeedsAssign(), new FakeCurrentUser(TenantRole.Technician, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(SecurityEventIds.AccessDenied, outcome.Log.Entries.Single().EventId.Id);
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

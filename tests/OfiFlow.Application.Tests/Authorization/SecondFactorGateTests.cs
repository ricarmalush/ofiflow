using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Authorization;

/// <summary>
/// La puerta del segundo factor obligatorio (spec 022, bloque 1) con dobles: lo que el <see cref="AuthorizationBehavior{TRequest,TResponse}"/> hace con quien debe activarlo
/// y todavía no lo ha hecho. Quién lo debe (la política por rol) lo prueban los tests de Domain; que el estado salga de la base de datos en la misma consulta, los de Infrastructure.
/// </summary>
public class SecondFactorGateTests
{
    [RequiresPermission(Permission.JobsRead)]
    private sealed record NeedsRead : IRequest<string>;

    [RequiresPermission(Permission.MfaManage)]
    [AllowedWithoutSecondFactor]
    private sealed record Allowed : IRequest<string>;

    [RequiresPermission(Permission.MfaManage)]
    private sealed record MarkedNowhere : IRequest<string>;

    [AllowAnonymousRequest]
    private sealed record Anonymous : IRequest<string>;

    [RequiresPlatformOperator]
    private sealed record OfThePlatform : IRequest<string>;

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

    private static FakeCurrentUser WithoutTheFactor(TenantRole role) =>
        new(role, Guid.NewGuid(), Guid.NewGuid(), secondFactorEnrollmentRequired: true);

    // ===== Lo que se deniega =====

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task WhoMustEnrollAndHasNot_IsDeniedAnythingNotMarkedAsAllowed_WithTheEnrollmentCode(TenantRole role)
    {
        var outcome = await Run(new NeedsRead(), WithoutTheFactor(role));

        Assert.Equal(MfaErrors.EnrollmentRequired, outcome.Denied?.Code);
        Assert.Equal("mfa.enrollment_required", outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task AnOperationWithAPermissionButNotMarked_IsDeniedToo_EvenIfItIsAnMfaOne()
    {
        // La lista de permitidas es CERRADA y se marca operación por operación: tener el permiso Mfa.Manage no basta (D2, fail-closed).
        var outcome = await Run(new MarkedNowhere(), WithoutTheFactor(TenantRole.Owner));

        Assert.Equal(MfaErrors.EnrollmentRequired, outcome.Denied?.Code);
        Assert.False(outcome.NextCalled);
    }

    [Fact]
    public async Task TheDenial_IsLoggedOnce_WithTheOperationTheIdsAndTheRole_AndNothingElse()
    {
        var user = WithoutTheFactor(TenantRole.Admin);

        var outcome = await Run(new NeedsRead(), user);

        var entry = Assert.Single(outcome.Log.Entries);
        Assert.Equal(SecurityEventIds.MfaEnrollmentRequiredDenied, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(NeedsRead), entry.Message);
        Assert.Contains(user.UserId!.Value.ToString(), entry.Message);
        Assert.Contains(user.TenantId!.Value.ToString(), entry.Message);
        Assert.Contains("Admin", entry.Message);
    }

    [Fact]
    public async Task TheDenialEvent_IsNotTheSameAsTheOrdinaryAccessDenied()
    {
        // Un analista distingue «no tiene permiso» (1301) de «le falta el factor» (1304) por el número, sin leer el texto.
        var outcome = await Run(new NeedsRead(), WithoutTheFactor(TenantRole.Owner));

        Assert.DoesNotContain(outcome.Log.Entries, entry => entry.EventId.Id == SecurityEventIds.AccessDenied);
        Assert.NotEqual(SecurityEventIds.AccessDenied, SecurityEventIds.MfaEnrollmentRequiredDenied);
    }

    // ===== Lo que se permite =====

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task WhoMustEnrollAndHasNot_CanDoTheOperationsMarkedAsAllowed(TenantRole role)
    {
        var outcome = await Run(new Allowed(), WithoutTheFactor(role));

        Assert.Equal("ejecutada", outcome.Result);
        Assert.True(outcome.NextCalled);
        Assert.Empty(outcome.Log.Entries);
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    public async Task WhoHasTheFactor_OrIsNotRequiredToHaveIt_DoesEverythingAsBefore(TenantRole role)
    {
        // El doble dice «no debe activarlo» (ya lo tiene, o su rol no lo exige): la puerta no interviene.
        var outcome = await Run(new NeedsRead(), new FakeCurrentUser(role, Guid.NewGuid(), Guid.NewGuid(), secondFactorEnrollmentRequired: false));

        Assert.Equal("ejecutada", outcome.Result);
        Assert.True(outcome.NextCalled);
        Assert.Empty(outcome.Log.Entries);
    }

    // ===== El orden de las comprobaciones =====

    [Fact]
    public async Task WhoLacksThePermission_GetsTheOrdinaryForbidden_WhetherOrNotTheyHaveTheFactor()
    {
        // ADR-012 R3: quien no puede hacer algo no recibe información sobre ello. Quien pide algo que su rol no puede recibe «auth.forbidden», NO «activa el segundo factor»:
        // eso último solo lo oye quien sí podría hacerlo si tuviera el factor.
        // Hoy ningún rol que exige el factor carece de un permiso (Owner y Admin tienen todos), así que el doble FUERZA el estado sobre un Manager (que no tiene Customers.Delete)
        // para fijar el ORDEN de las dos comprobaciones: si alguien pusiera la puerta antes del permiso, este test falla aunque hoy no se pueda dar en producción.
        var withoutFactor = await Run(new NeedsDeleteCustomer(), WithoutTheFactor(TenantRole.Manager));
        var withFactor = await Run(new NeedsDeleteCustomer(), new FakeCurrentUser(TenantRole.Manager, Guid.NewGuid(), Guid.NewGuid(), secondFactorEnrollmentRequired: false));

        Assert.Equal(TenancyErrors.Forbidden, withoutFactor.Denied?.Code);
        Assert.Equal(TenancyErrors.Forbidden, withFactor.Denied?.Code);
        Assert.Contains(withoutFactor.Log.Entries, entry => entry.EventId.Id == SecurityEventIds.AccessDenied);
        Assert.DoesNotContain(withoutFactor.Log.Entries, entry => entry.EventId.Id == SecurityEventIds.MfaEnrollmentRequiredDenied);
    }

    [RequiresPermission(Permission.CustomersDelete)]
    private sealed record NeedsDeleteCustomer : IRequest<string>;

    [Fact]
    public async Task TheMembershipIsVerifiedBeforeTheGateIsRead()
    {
        // El dato del factor solo vale tras verificar la pertenencia (como el rol): el comportamiento verifica una sola vez y después decide.
        var user = WithoutTheFactor(TenantRole.Owner);
        Assert.False(user.SecondFactorEnrollmentRequired);

        await Run(new NeedsRead(), user);

        Assert.Equal(1, user.VerifyCalls);
        Assert.True(user.SecondFactorEnrollmentRequired);
    }

    [Fact]
    public async Task WhoIsNoLongerAMember_GetsTheMembershipDenial_NotTheEnrollmentOne()
    {
        // Sin rol efectivo no hay nada que exigir: la denegación es la de siempre.
        var outcome = await Run(new NeedsRead(), new FakeCurrentUser(role: null, Guid.NewGuid(), Guid.NewGuid(), secondFactorEnrollmentRequired: true));

        Assert.Equal(TenancyErrors.Forbidden, outcome.Denied?.Code);
        Assert.Contains(outcome.Log.Entries, entry => entry.EventId.Id == SecurityEventIds.MembershipRevoked);
        Assert.DoesNotContain(outcome.Log.Entries, entry => entry.EventId.Id == SecurityEventIds.MfaEnrollmentRequiredDenied);
    }

    // ===== Lo que no pasa por la puerta =====

    [Fact]
    public async Task AnAnonymousOperation_NeverReachesTheGate()
    {
        var user = WithoutTheFactor(TenantRole.Owner);

        var outcome = await Run(new Anonymous(), user);

        Assert.Equal("ejecutada", outcome.Result);
        Assert.Equal(0, user.VerifyCalls);
    }

    [Fact]
    public async Task APlatformOperation_IsNotAffected_ThePlatformIsAnotherWorld()
    {
        var user = new FakeCurrentUser(role: null, Guid.NewGuid(), tenantId: null, platformSession: true, platformOperatorActive: true, secondFactorEnrollmentRequired: true);

        var outcome = await Run(new OfThePlatform(), user);

        Assert.Equal("ejecutada", outcome.Result);
        Assert.Equal(0, user.VerifyCalls);
    }
}

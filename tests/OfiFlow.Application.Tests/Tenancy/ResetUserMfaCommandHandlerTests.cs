using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Tenancy.Commands.ResetUserMfa;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

/// <summary>
/// Reiniciar el segundo factor de otro miembro (spec 010): sigue la jerarquía de la spec 009 y cierra las sesiones de
/// quien lo pierde. Que el miembro de OTRA empresa dé el mismo 404 que uno inexistente lo hace el filtro de empresa,
/// que esta base en memoria no tiene: se prueba contra SQL Server real (Infrastructure.Tests) y por HTTP.
/// </summary>
public class ResetUserMfaCommandHandlerTests : MemberManagementTestBase
{
    private readonly FakeMfaService _mfa = new();
    private readonly FakeTokenService _tokens = new();
    private readonly ListLogger<ResetUserMfaCommandHandler> _logger = new();

    private ResetUserMfaCommandHandler Handler(TenantRole actor) => new(Db, Actor(actor), _mfa, _tokens, _logger);

    private Task ResetAsync(TenantRole actor, Guid tenantUserId) =>
        Handler(actor).Handle(new ResetUserMfaCommand(tenantUserId), CancellationToken.None);

    // ===== Lo que hace =====

    [Fact]
    public async Task TheOwner_ResetsAMembersMfa_RemovingItsSecretAndClosingItsSessions()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician);
        _mfa.Set(target.UserId, new OfiFlow.Application.Common.Abstractions.MfaStatus(true, false, 4));

        await ResetAsync(TenantRole.Owner, target.Id);

        Assert.Equal([target.UserId], _mfa.RemovedAll);
        Assert.Equal([target.UserId], _tokens.UsersWithDeletedTokens);
    }

    [Fact]
    public async Task OnlyTheTargetIsAffected_NotTheActorNorTheOthers()
    {
        var actor = await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Employee);
        var bystander = await SeedMemberAsync(TenantRole.Manager);

        await ResetAsync(TenantRole.Owner, target.Id);

        Assert.DoesNotContain(actor.UserId, _mfa.RemovedAll);
        Assert.DoesNotContain(bystander.UserId, _mfa.RemovedAll);
        Assert.DoesNotContain(bystander.UserId, _tokens.UsersWithDeletedTokens);
    }

    [Fact]
    public async Task ResettingAMemberWithoutMfa_IsIdempotent_AndStillClosesTheSessions()
    {
        // No falla: quien reinicia no ve el estado de otro, y repetirlo por un reintento no debe dar un error.
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician);

        await ResetAsync(TenantRole.Owner, target.Id);

        Assert.Equal([target.UserId], _mfa.RemovedAll);
    }

    [Fact]
    public async Task AnUnknownMember_IsNotFound_AndNothingIsTouched()
    {
        await SeedActorAsync(TenantRole.Owner);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => ResetAsync(TenantRole.Owner, Guid.NewGuid()));

        Assert.Equal(TenancyErrors.TenantUserNotFound, exception.Code);
        Assert.Empty(_mfa.RemovedAll);
        Assert.Empty(_tokens.UsersWithDeletedTokens);
    }

    // ===== La jerarquía (spec 009 R9, R11, R12) =====

    public static TheoryData<TenantRole, TenantRole, bool> Hierarchy()
    {
        // Copia literal de la jerarquía aprobada: quién puede reiniciar a quién.
        var data = new TheoryData<TenantRole, TenantRole, bool>();

        foreach (var target in new[] { TenantRole.Admin, TenantRole.Manager, TenantRole.Technician, TenantRole.Employee })
        {
            data.Add(TenantRole.Owner, target, true);
        }

        data.Add(TenantRole.Owner, TenantRole.Owner, false);

        foreach (var target in new[] { TenantRole.Manager, TenantRole.Technician, TenantRole.Employee })
        {
            data.Add(TenantRole.Admin, target, true);
        }

        data.Add(TenantRole.Admin, TenantRole.Owner, false);
        data.Add(TenantRole.Admin, TenantRole.Admin, false);

        foreach (var actor in new[] { TenantRole.Manager, TenantRole.Technician, TenantRole.Employee })
        {
            foreach (var target in Enum.GetValues<TenantRole>())
            {
                data.Add(actor, target, false);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Hierarchy))]
    public async Task EveryCellOfTheHierarchy_IsEnforced(TenantRole actor, TenantRole targetRole, bool allowed)
    {
        await SeedActorAsync(actor);
        var target = await SeedMemberAsync(targetRole);

        if (allowed)
        {
            await ResetAsync(actor, target.Id);

            Assert.Equal([target.UserId], _mfa.RemovedAll);
            Assert.Equal([target.UserId], _tokens.UsersWithDeletedTokens);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ForbiddenException>(() => ResetAsync(actor, target.Id));

            Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
            Assert.Empty(_mfa.RemovedAll);
            Assert.Empty(_tokens.UsersWithDeletedTokens);
        }
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task NobodyResetsTheirOwnMfa_ByThisRoute(TenantRole role)
    {
        // Reiniciar no pide contraseña ni código; con un token robado, uno podría quitarse el segundo factor.
        // Para uno mismo está desactivar, que sí los pide.
        var me = await SeedActorAsync(role);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => ResetAsync(role, me.Id));

        Assert.Equal(TenancyErrors.CannotManageSelf, exception.Code);
        Assert.Empty(_mfa.RemovedAll);
    }

    [Fact]
    public async Task ADeniedReset_IsLoggedAsAnAccessDenial_WithTheReason()
    {
        await SeedActorAsync(TenantRole.Admin);
        var otherAdmin = await SeedMemberAsync(TenantRole.Admin);

        await Assert.ThrowsAsync<ForbiddenException>(() => ResetAsync(TenantRole.Admin, otherAdmin.Id));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.AccessDenied);
        Assert.Contains(nameof(ResetUserMfaCommand), entry.Message);
    }

    // ===== Registro =====

    [Fact]
    public async Task TheResetEvent_CarriesOnlyIds_NeverNameNorEmail()
    {
        await SeedActorAsync(TenantRole.Owner);
        var target = await SeedMemberAsync(TenantRole.Technician, name: "Luis Pérez", email: "luis@example.com");

        await ResetAsync(TenantRole.Owner, target.Id);

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaResetByManager);
        Assert.Contains(target.Id.ToString(), entry.Message);
        Assert.Contains(ActorUserId.ToString(), entry.Message);
        Assert.DoesNotContain("Luis", entry.Message);
        Assert.DoesNotContain("luis@example.com", entry.Message);
    }
}

using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

public class InvitationTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private static Invitation NewInvitation(TenantRole role = TenantRole.Technician, string email = "Ana@Example.com", string hash = "hash-de-prueba") =>
        Invitation.Create(Guid.NewGuid(), Email.Create(email), role, hash, Guid.NewGuid(), Now);

    // ===== Crear =====

    [Fact]
    public void Create_StartsPending_AndCopiesTheData()
    {
        var tenantId = Guid.NewGuid();
        var createdBy = Guid.NewGuid();

        var invitation = Invitation.Create(tenantId, Email.Create("Ana@Example.com"), TenantRole.Manager, "el-hash", createdBy, Now);

        Assert.Equal(tenantId, invitation.TenantId);
        Assert.Equal(createdBy, invitation.CreatedByUserId);
        Assert.Equal(TenantRole.Manager, invitation.Role);
        Assert.Equal("el-hash", invitation.TokenHash);
        Assert.Equal("Ana@Example.com", invitation.Email.Value);
        Assert.Equal(InvitationStatus.Pending, invitation.StatusAt(Now));
        Assert.Null(invitation.AcceptedAt);
        Assert.Null(invitation.RevokedAt);
    }

    [Fact]
    public void Create_NormalizesTheEmailToUpperCase_ForCaseInsensitiveLookups()
    {
        var invitation = NewInvitation(email: "Ana@Example.com");

        Assert.Equal("ANA@EXAMPLE.COM", invitation.NormalizedEmail);
    }

    [Fact]
    public void Create_ExpiresSevenDaysAfterTheGivenTime()
    {
        var invitation = NewInvitation();

        Assert.Equal(Now.AddDays(7), invitation.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(7), Invitation.Lifetime);
    }

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public void Create_AcceptsEveryRoleThatCanBeGranted(TenantRole role)
    {
        var invitation = NewInvitation(role);

        Assert.Equal(role, invitation.Role);
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData((TenantRole)999)]
    public void Create_RejectsOwnerAndRolesThatDoNotExist(TenantRole role)
    {
        // R1: el Owner no se concede nunca, ni a través de una invitación.
        var exception = Assert.Throws<DomainException>(() => NewInvitation(role));

        Assert.Equal(TenancyErrors.RoleNotManageable, exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsABlankTokenHash(string hash)
    {
        Assert.ThrowsAny<ArgumentException>(() => NewInvitation(hash: hash));
    }

    [Fact]
    public void Create_RejectsATokenHashLongerThanTheColumn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewInvitation(hash: new string('a', Invitation.TokenHashMaxLength + 1)));
    }

    [Theory]
    [InlineData("Ana@Example.com", "ANA@EXAMPLE.COM")]
    [InlineData("ANA@EXAMPLE.COM", "ANA@EXAMPLE.COM")]
    [InlineData("ana@example.com", "ANA@EXAMPLE.COM")]
    public void NormalizeEmail_IsTheSameRuleTheInvitationUsesToStoreIt(string input, string expected)
    {
        var email = Email.Create(input);

        Assert.Equal(expected, Invitation.NormalizeEmail(email));
        Assert.Equal(expected, Invitation.Create(Guid.NewGuid(), email, TenantRole.Technician, "hash", Guid.NewGuid(), Now).NormalizedEmail);
    }

    // ===== Estado y caducidad =====

    [Fact]
    public void IsPending_UntilTheExpirationInstant_ThenExpired()
    {
        var invitation = NewInvitation();

        Assert.Equal(InvitationStatus.Pending, invitation.StatusAt(invitation.ExpiresAt.AddTicks(-1)));
        // En el mismo instante de la caducidad ya no sirve (límite exclusivo): ante la duda, se rechaza.
        Assert.Equal(InvitationStatus.Expired, invitation.StatusAt(invitation.ExpiresAt));
        Assert.Equal(InvitationStatus.Expired, invitation.StatusAt(invitation.ExpiresAt.AddDays(30)));
    }

    // ===== Aceptar =====

    [Fact]
    public void Accept_MarksItAccepted_AndItIsNoLongerPending()
    {
        var invitation = NewInvitation();
        var acceptedAt = Now.AddHours(2);

        invitation.Accept(acceptedAt);

        Assert.Equal(acceptedAt, invitation.AcceptedAt);
        Assert.Equal(InvitationStatus.Accepted, invitation.StatusAt(acceptedAt));
        Assert.False(invitation.IsPendingAt(acceptedAt));
    }

    [Fact]
    public void Accept_CannotBeDoneTwice_OneUseOnly()
    {
        var invitation = NewInvitation();
        invitation.Accept(Now.AddHours(1));

        var exception = Assert.Throws<DomainException>(() => invitation.Accept(Now.AddHours(2)));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Equal(Now.AddHours(1), invitation.AcceptedAt); // no se sobrescribe
    }

    [Fact]
    public void Accept_FailsOnceExpired_AndChangesNothing()
    {
        var invitation = NewInvitation();

        var exception = Assert.Throws<DomainException>(() => invitation.Accept(invitation.ExpiresAt));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Null(invitation.AcceptedAt);
    }

    [Fact]
    public void Accept_FailsOnceRevoked()
    {
        var invitation = NewInvitation();
        invitation.Revoke(Now.AddHours(1));

        var exception = Assert.Throws<DomainException>(() => invitation.Accept(Now.AddHours(2)));

        Assert.Equal(InvitationErrors.Invalid, exception.Code);
        Assert.Null(invitation.AcceptedAt);
    }

    // ===== Revocar =====

    [Fact]
    public void Revoke_MarksItRevoked()
    {
        var invitation = NewInvitation();
        var revokedAt = Now.AddHours(1);

        invitation.Revoke(revokedAt);

        Assert.Equal(revokedAt, invitation.RevokedAt);
        Assert.Equal(InvitationStatus.Revoked, invitation.StatusAt(revokedAt));
    }

    [Fact]
    public void Revoke_FailsIfAlreadyAccepted_RevokedOrExpired()
    {
        var accepted = NewInvitation();
        accepted.Accept(Now.AddHours(1));

        var revoked = NewInvitation();
        revoked.Revoke(Now.AddHours(1));

        var expired = NewInvitation();

        Assert.Equal(InvitationErrors.Invalid, Assert.Throws<DomainException>(() => accepted.Revoke(Now.AddHours(2))).Code);
        Assert.Equal(InvitationErrors.Invalid, Assert.Throws<DomainException>(() => revoked.Revoke(Now.AddHours(2))).Code);
        Assert.Equal(InvitationErrors.Invalid, Assert.Throws<DomainException>(() => expired.Revoke(expired.ExpiresAt)).Code);
        Assert.Null(accepted.RevokedAt);
    }

    // ===== Lo definitivo manda sobre la caducidad =====

    [Fact]
    public void AnAcceptedInvitation_StaysAccepted_AfterItsExpirationDate()
    {
        var invitation = NewInvitation();
        invitation.Accept(Now.AddHours(1));

        Assert.Equal(InvitationStatus.Accepted, invitation.StatusAt(invitation.ExpiresAt.AddDays(365)));
    }

    [Fact]
    public void ARevokedInvitation_StaysRevoked_AfterItsExpirationDate()
    {
        var invitation = NewInvitation();
        invitation.Revoke(Now.AddHours(1));

        Assert.Equal(InvitationStatus.Revoked, invitation.StatusAt(invitation.ExpiresAt.AddDays(365)));
    }

    // ===== Datos de la tabla =====

    [Fact]
    public void ItIsATenantOwnedAggregate_SoTheTenantFilterApplies()
    {
        // Lo exige el aislamiento entre empresas (spec 007): una entidad de empresa implementa ITenantOwned.
        Assert.IsAssignableFrom<ITenantOwned>(NewInvitation());
    }

    [Fact]
    public void ThePendingLimit_IsTwentyFive()
    {
        Assert.Equal(25, Invitation.MaxPendingPerTenant);
    }
}

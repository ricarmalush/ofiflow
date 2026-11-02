using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tenancy.Commands.InviteUser;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

/// <summary>
/// Invitar con el correo configurado (spec 017, bloque 2): el enlace se envía, quien invita no conoce el token, y si el envío falla no se crea nada. El comportamiento sin correo
/// (la spec 009) lo fija <see cref="InviteUserCommandHandlerTests"/>.
/// </summary>
public class InviteUserByEmailTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenantId;
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeIdentityService _identity = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ListLogger<InviteUserCommandHandler> _logger = new();
    private readonly FakeEmailSender _email = new(isEnabled: true);

    public InviteUserByEmailTests()
    {
        // Una empresa AJENA, guardada la primera: una consulta que cogiera «la primera empresa» en vez de la del contexto se equivocaría.
        _db.Tenants.Add(Tenant.Create("Empresa Ajena S.L."));

        // El nombre de la empresa sale de la base de datos; el contexto de empresa de la prueba es esa misma.
        var tenant = Tenant.Create("Fontanería Pérez");
        _tenantId = tenant.Id;
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
    }

    private InviteUserCommandHandler HandlerFor(TenantRole actorRole) =>
        new(_db, new FakeTenantContext(_tenantId), new FakeCurrentUser(actorRole, _actorId, _tenantId), _identity, _email, new FakeEmailLinks(), _time, _logger);

    private Task<InvitationCreatedDto> InviteAsync(string email, TenantRole role, TenantRole actor = TenantRole.Owner) =>
        HandlerFor(actor).Handle(new InviteUserCommand(email, role), CancellationToken.None);

    // ===== Camino feliz =====

    [Fact]
    public async Task WithEmail_TheLinkIsSent_AndTheResponseCarriesNoToken()
    {
        var created = await InviteAsync("ana@example.com", TenantRole.Technician);

        Assert.True(created.EmailSent);
        Assert.Null(created.Token);
        var message = Assert.Single(_email.Sent);
        Assert.Equal("ana@example.com", message.To.Value);
        Assert.Equal(InvitationEmail.Subject, message.Subject);
        Assert.StartsWith($"{FakeEmailLinks.BaseUrl}/auth/accept-invitation#token=", ExtractLink(message));
    }

    [Fact]
    public async Task TheTokenInTheEmail_IsTheOneWhoseHashIsStored()
    {
        // Lo importante: el enlace enviado sirve de verdad para esta invitación (lo único que une el correo con lo guardado es el hash).
        await InviteAsync("ana@example.com", TenantRole.Technician);

        var token = ExtractLink(Assert.Single(_email.Sent))[(FakeEmailLinks.BaseUrl + "/auth/accept-invitation#token=").Length..];
        var stored = await _db.Invitations.SingleAsync();
        Assert.True(OpaqueToken.IsWellFormed(token));
        Assert.Equal(OpaqueToken.Hash(token), stored.TokenHash);
    }

    [Fact]
    public async Task TheEmailNamesTheCompany_TheRole_AndTheExpiry_AndNothingTheInviterTyped()
    {
        await InviteAsync("ana@example.com", TenantRole.Technician);

        var body = Assert.Single(_email.Sent).TextBody;
        Assert.Contains("«Fontanería Pérez»", body);
        Assert.Contains("rol de técnico", body);
        Assert.Contains("22 de octubre de 2026", body); // Now + 7 días
    }

    [Fact]
    public async Task TheInvitationIsStoredOnlyOnceTheEmailWasAccepted()
    {
        // R11, D3: se envía ANTES de guardar. En el momento de entregar el correo todavía no hay nada guardado.
        var storedAtSendTime = -1;
        _email.OnSend = async () => storedAtSendTime = await _db.Invitations.CountAsync();

        await InviteAsync("ana@example.com", TenantRole.Technician);

        Assert.Equal(0, storedAtSendTime);
        Assert.Equal(1, await _db.Invitations.CountAsync());
    }

    // ===== Si el envío falla =====

    [Theory]
    [InlineData(EmailFailureCategory.Timeout)]
    [InlineData(EmailFailureCategory.Authentication)]
    [InlineData(EmailFailureCategory.Rejected)]
    [InlineData(EmailFailureCategory.Unavailable)]
    [InlineData(EmailFailureCategory.Unknown)]
    public async Task WhenTheEmailCannotBeSent_NothingIsCreated_AndTheErrorSaysItWasNotSent(EmailFailureCategory category)
    {
        _email.FailWith = category;

        var exception = await Assert.ThrowsAsync<ExternalServiceException>(() => InviteAsync("ana@example.com", TenantRole.Technician));

        Assert.Equal(InvitationErrors.EmailNotSent, exception.Code);
        Assert.False(await _db.Invitations.AnyAsync());
    }

    [Fact]
    public async Task AFailedSend_IsLoggedAsAWarningWithTheCategory_AndNoPersonalData()
    {
        _email.FailWith = EmailFailureCategory.Authentication;

        await Assert.ThrowsAsync<ExternalServiceException>(() => InviteAsync("secreta@example.com", TenantRole.Technician));

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationEmailFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Authentication", entry.Message);
        Assert.Contains(_tenantId.ToString(), entry.Message);
        Assert.Contains(_actorId.ToString(), entry.Message);
        Assert.DoesNotContain("secreta", entry.Message);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id is SecurityEventIds.InvitationEmailSent or SecurityEventIds.InvitationCreated);
    }

    [Fact]
    public async Task AFailedSend_DoesNotBlockTryingAgain()
    {
        _email.FailWith = EmailFailureCategory.Timeout;
        await Assert.ThrowsAsync<ExternalServiceException>(() => InviteAsync("ana@example.com", TenantRole.Technician));

        _email.FailWith = null;
        var created = await InviteAsync("ana@example.com", TenantRole.Technician);

        Assert.True(created.EmailSent);
        Assert.Equal(1, await _db.Invitations.CountAsync());
    }

    // ===== Eventos de un envío correcto =====

    [Fact]
    public async Task ASuccessfulSend_LeavesOneEvent_WithIdsOnly()
    {
        var created = await InviteAsync("secreta@example.com", TenantRole.Technician);

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.InvitationEmailSent);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(created.Id.ToString(), entry.Message);
        Assert.Contains(_tenantId.ToString(), entry.Message);
        Assert.Contains(_actorId.ToString(), entry.Message);

        var token = ExtractLink(Assert.Single(_email.Sent))[(FakeEmailLinks.BaseUrl + "/auth/accept-invitation#token=").Length..];
        var everything = string.Join(" ", _logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("secreta", everything);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(FakeEmailLinks.BaseUrl, everything);
    }

    // ===== R13: no se envía correo por una invitación que se iba a rechazar =====

    [Fact]
    public async Task WhenTheHierarchyForbidsIt_NoEmailIsSent()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => InviteAsync("ana@example.com", TenantRole.Admin, actor: TenantRole.Admin));

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task WhenTheEmailAlreadyHasAnAccount_NoEmailIsSent()
    {
        _identity.AddExistingAccount("ana@example.com");

        await Assert.ThrowsAsync<IdentityOperationException>(() => InviteAsync("ana@example.com", TenantRole.Technician));

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task WhenOneIsAlreadyPending_NoSecondEmailIsSent()
    {
        await InviteAsync("ana@example.com", TenantRole.Technician);

        await Assert.ThrowsAsync<BusinessRuleException>(() => InviteAsync("ANA@example.com", TenantRole.Manager));

        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task WhenTheLimitOfPendingIsReached_NoEmailIsSent()
    {
        for (var i = 0; i < Invitation.MaxPendingPerTenant; i++)
        {
            await InviteAsync($"persona{i}@example.com", TenantRole.Employee);
        }

        var sentBefore = _email.Sent.Count;
        await Assert.ThrowsAsync<BusinessRuleException>(() => InviteAsync("una-mas@example.com", TenantRole.Employee));

        Assert.Equal(sentBefore, _email.Sent.Count);
    }

    // ===== Aislamiento entre empresas =====

    [Fact]
    public async Task TheCompanyInTheEmail_IsTheInvitersOwn_NeverAnotherOne()
    {
        await InviteAsync("ana@example.com", TenantRole.Technician);

        var body = Assert.Single(_email.Sent).TextBody;
        Assert.Contains("Fontanería Pérez", body);
        Assert.DoesNotContain("Empresa Ajena", body);
    }

    private static string ExtractLink(EmailMessage message) =>
        message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));
}

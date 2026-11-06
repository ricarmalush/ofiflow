using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.CompleteRegistration;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Completar el registro (spec 021, bloque 3) con dobles: lo que el caso de uso <b>orquesta</b> (canjear, crear la cuenta, la empresa, el usuario y el <c>Owner</c>, confirmar UNA vez) y lo que
/// registra. Que la base de datos de verdad lo haga todo o nada, y que dos canjes a la vez los resuelva la versión de fila, lo prueban los tests de Infrastructure y de Api contra SQL Server.
/// </summary>
public class CompleteRegistrationCommandHandlerTests
{
    private const string Address = "Ana.Secreta@Example.com";
    private const string Company = "Fontanería Secretísima SL";
    private const string Person = "Ana Pérez Secreta";
    private const string Password = "Clave-Elegida-MUY-secreta-2026!";
    private const string Version = "2026-11";
    private static readonly DateTime AcceptedAt = new(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);
    private static readonly string Token = OpaqueToken.Generate().Value;

    private readonly FakePendingRegistrationService _pending = new();
    private readonly FakeIdentityService _identity = new();
    private readonly ListLogger<CompleteRegistrationCommandHandler> _logger = new();
    private readonly TestDbContext _db = TestDbContextFactory.Create();

    public CompleteRegistrationCommandHandlerTests() =>
        _pending.ConsumeResult = new(PendingRegistrationConsumeOutcome.Consumed, Address, Company, Person, Version, AcceptedAt);

    private Task CompleteAsync(string? token = null, string password = Password) =>
        new CompleteRegistrationCommandHandler(_pending, _identity, _db, _logger)
            .Handle(new CompleteRegistrationCommand(token ?? Token, password), CancellationToken.None);

    private static void AssertTheSingleError(DomainException exception) => Assert.Equal(IdentityErrors.RegistrationInvalid, exception.Code);

    // ===== El camino feliz: nace todo, y una sola confirmación =====

    [Fact]
    public async Task AValidToken_CreatesTheCompany_TheUserAndTheOwnerMembership()
    {
        await CompleteAsync();

        var tenant = await _db.Tenants.SingleAsync();
        var user = await _db.Users.SingleAsync();
        var membership = await _db.TenantUsers.SingleAsync();

        Assert.Equal(Company, tenant.Name);
        Assert.Equal(Person, user.Name);
        Assert.Equal(Address, user.ContactEmail.Value);
        Assert.Equal(tenant.Id, membership.TenantId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(TenantRole.Owner, membership.Role);
    }

    [Fact]
    public async Task TheTermsAcceptedWhenRequesting_PassToTheUser_WithThatMoment_NotTheMomentOfCompleting()
    {
        // R10, R16: lo que la persona aceptó al pedir el registro, y cuándo. No la hora de abrir el enlace.
        await CompleteAsync();

        var user = await _db.Users.SingleAsync();
        Assert.Equal(Version, user.TermsVersion);
        Assert.Equal(AcceptedAt, user.TermsAcceptedAt);
    }

    [Fact]
    public async Task TheAccountIsCreatedWithThePasswordChosenNow_AndNoOtherEmail()
    {
        await CompleteAsync();

        var user = await _db.Users.SingleAsync();
        Assert.Equal(user.Id, (await _identity.AuthenticateAsync(Address, Password, CancellationToken.None))?.UserId);
        Assert.Null(await _identity.AuthenticateAsync(Address, "otra-clave-distinta", CancellationToken.None));
    }

    [Fact]
    public async Task EverythingIsConfirmedTogether_InASingleSave()
    {
        // R10: una cuenta sin empresa, o una empresa sin dueño, o un pendiente gastado sin cuenta, no pueden existir. Una sola confirmación.
        await CompleteAsync();

        Assert.Equal(1, _db.SaveCount);
    }

    [Fact]
    public async Task TheTokenIsConsumedOnce_WithTheTokenTheLinkCarried()
    {
        await CompleteAsync();

        Assert.Equal([Token], _pending.Consumed);
    }

    [Fact]
    public async Task OnSuccess_TheCompletionIsLogged_WithTheNewIds_AndNothingPersonal()
    {
        await CompleteAsync();

        var tenant = await _db.Tenants.SingleAsync();
        var user = await _db.Users.SingleAsync();
        var entry = Assert.Single(_logger.Entries);

        Assert.Equal(SecurityEventIds.RegistrationCompleted, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(tenant.Id.ToString(), entry.Message);
        Assert.Contains(user.Id.ToString(), entry.Message);
        AssertNoSecrets(entry.Message);
    }

    // ===== Un token que no sirve: el mismo error, y no cambia nada =====

    [Theory]
    [InlineData("Unknown")]
    [InlineData("UsedOrReplaced")]
    [InlineData("Expired")]
    [InlineData("EmailTaken")]
    public async Task ATokenThatDoesNotServe_AlwaysGivesTheSameError_WhateverTheRealReason(string reason)
    {
        _pending.ConsumeResult = new(PendingRegistrationConsumeOutcome.Rejected, RejectedReason: reason);

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync());

        AssertTheSingleError(error);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("UsedOrReplaced")]
    [InlineData("Expired")]
    [InlineData("EmailTaken")]
    public async Task ARejectedToken_CreatesNothing_ConfirmsNothing_AndNeverPaysForTheSlowPasswordHash(string reason)
    {
        _pending.ConsumeResult = new(PendingRegistrationConsumeOutcome.Rejected, RejectedReason: reason);

        await Assert.ThrowsAsync<DomainException>(() => CompleteAsync());

        Assert.False(await _db.Tenants.AnyAsync());
        Assert.False(await _db.Users.AnyAsync());
        Assert.False(await _db.TenantUsers.AnyAsync());
        Assert.Equal(0, _db.SaveCount);

        // El hash lento de la contraseña (y la cuenta de acceso) ni se llegan a pagar: este endpoint es anónimo.
        Assert.False(await _identity.IsEmailRegisteredAsync(Address, CancellationToken.None));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("UsedOrReplaced")]
    [InlineData("Expired")]
    [InlineData("EmailTaken")]
    public async Task TheRealReason_GoesToTheLog_AndOnlyThere(string reason)
    {
        _pending.ConsumeResult = new(PendingRegistrationConsumeOutcome.Rejected, RejectedReason: reason);

        await Assert.ThrowsAsync<DomainException>(() => CompleteAsync());

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(SecurityEventIds.RegistrationRejected, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"Motivo {reason}", entry.Message);
        AssertNoSecrets(entry.Message);
    }

    [Fact]
    public async Task ARejectionWithoutAReason_IsLoggedAsUnknown_NeverAsNothing()
    {
        _pending.ConsumeResult = new(PendingRegistrationConsumeOutcome.Rejected);

        await Assert.ThrowsAsync<DomainException>(() => CompleteAsync());

        Assert.Contains("Motivo Unknown", Assert.Single(_logger.Entries).Message);
    }

    // ===== Carreras: el email se ocupó entre medias, o dos canjes a la vez =====

    [Fact]
    public async Task IfTheEmailGetsTakenBetweenTheCheckAndTheCreation_ItIsTheSameErrorAndNothingIsConfirmed()
    {
        // Alguien creó la cuenta (por una invitación, por ejemplo) justo después de que el pendiente comprobara el email. Es «este enlace ya no sirve», no un error corregible.
        _identity.ShouldFailCreation = true;

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync());

        AssertTheSingleError(error);
        Assert.Equal(0, _db.SaveCount);
        Assert.False(await _db.Tenants.AnyAsync());
        Assert.Contains("Motivo EmailTaken", Assert.Single(_logger.Entries).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IfTheDatabaseRefusesTheSave_ItIsTheSameErrorAsABadToken_NeverA500(bool rowVersionConflict)
    {
        // R13: dos peticiones con el mismo token. La base de datos deja pasar a una y rechaza a la otra, de una de dos formas (la versión de fila del pendiente, o el índice único del email):
        // las dos significan «este enlace ya no sirve» y reciben la misma respuesta. Capturar solo la de concurrencia dejaría escapar la otra como un 500.
        _db.ExceptionOnSave = rowVersionConflict ? new DbUpdateConcurrencyException("fallo") : new DbUpdateException("fallo");

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync());

        AssertTheSingleError(error);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(SecurityEventIds.RegistrationRejected, entry.EventId.Id);
        Assert.Contains("Motivo Concurrent", entry.Message);
        Assert.DoesNotContain("fallo", entry.Message);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsNotSwallowed()
    {
        // Solo los fallos de la base de datos son «el enlace ya no sirve». Cualquier otro (un bug, un corte) sigue siendo un error real y no se disfraza.
        _db.ExceptionOnSave = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync());
    }

    // ===== Nada secreto en ningún sitio =====

    [Fact]
    public void TheCommand_NeverPrintsItsSecrets()
    {
        var text = new CompleteRegistrationCommand(Token, Password).ToString();

        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain(Password, text);
    }

    private static void AssertNoSecrets(string message)
    {
        // ADR-009 R5, spec 021 R14: ni la contraseña, ni el token, ni el email, ni los nombres.
        Assert.DoesNotContain(Password, message);
        Assert.DoesNotContain(Token, message);
        Assert.DoesNotContain(Address, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Company, message);
        Assert.DoesNotContain(Person, message);
    }

    // ===== El validador =====

    public class Validation
    {
        private readonly CompleteRegistrationCommandValidator _validator = new();

        private static CompleteRegistrationCommand Valid() => new(OpaqueToken.Generate().Value, "una-clave-larga-123");

        [Fact]
        public void AValidCommand_Passes() => _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

        [Theory]
        [InlineData("")]
        [InlineData("corto")]
        [InlineData("esto-no-tiene-la-longitud-de-un-token-real")]
        [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
        public void AMalformedToken_HasTheSameCodeAsAnyOtherTokenFailure(string token)
        {
            var result = _validator.TestValidate(Valid() with { Token = token });

            result.ShouldHaveValidationErrorFor(x => x.Token).WithErrorCode(IdentityErrors.RegistrationInvalid);
        }

        [Fact]
        public void AnEnormousToken_IsRejectedWithoutBeingProcessed() =>
            _validator.TestValidate(Valid() with { Token = new string('A', 1_000_000) }).ShouldHaveValidationErrorFor(x => x.Token);

        [Theory]
        [InlineData(PasswordRules.MinLength - 1, false)]
        [InlineData(PasswordRules.MinLength, true)]
        [InlineData(PasswordRules.MaxLength, true)]
        [InlineData(PasswordRules.MaxLength + 1, false)]
        public void ThePassword_RespectsTheSameMinAndMaxAsAlways(int length, bool isValid)
        {
            var result = _validator.TestValidate(Valid() with { NewPassword = new string('a', length) });

            if (isValid)
            {
                result.ShouldNotHaveValidationErrorFor(x => x.NewPassword);
            }
            else
            {
                result.ShouldHaveValidationErrorFor(x => x.NewPassword);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AnEmptyPassword_IsRejected(string? password) =>
            _validator.TestValidate(Valid() with { NewPassword = password! }).ShouldHaveValidationErrorFor(x => x.NewPassword);

        [Fact]
        public void ABadPassword_IsAFieldError_NotTheTokenError()
        {
            // Una contraseña mala da un 400 de CAMPO y el enlace sigue sirviendo (no se gasta): el validador corre antes de tocar el token.
            var result = _validator.TestValidate(Valid() with { NewPassword = "corta" });

            result.ShouldNotHaveValidationErrorFor(x => x.Token);
        }
    }
}

using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.ResetPassword;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Restablecer la contraseña (spec 018, bloque 3) con dobles: lo que el caso de uso <b>orquesta</b> (consumir, cambiar, cerrar sesiones, confirmar UNA vez, avisar) y lo que registra. Que la
/// base de datos de verdad lo haga todo o nada, y que dos canjes a la vez los resuelva la versión de fila, lo prueban los tests de Infrastructure y de Api contra SQL Server.
/// </summary>
public class ResetPasswordCommandHandlerTests
{
    private const string Address = "ana.secreta@example.com";
    private const string NewPassword = "Clave-Nueva-MUY-secreta-2026!";
    private static readonly string Token = OpaqueToken.Generate().Value;
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakePasswordResetService _resets = new();
    private readonly FakeIdentityService _identity = new();
    private readonly FakeTokenService _tokens = new();
    private readonly FakeEmailQueue _queue = new();
    private readonly ListLogger<ResetPasswordCommandHandler> _logger = new();
    private readonly TestDbContext _db = TestDbContextFactory.Create();

    public ResetPasswordCommandHandlerTests()
    {
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Consumed, UserId);
        _identity.EmailOfTheAccountWhosePasswordIsSet = Address;
    }

    private Task ResetAsync(string? token = null, string password = NewPassword) =>
        new ResetPasswordCommandHandler(_resets, _identity, _tokens, _db, _queue, _logger)
            .Handle(new ResetPasswordCommand(token ?? Token, password), CancellationToken.None);

    private static void AssertTheSingleError(DomainException exception) => Assert.Equal(IdentityErrors.PasswordResetInvalid, exception.Code);

    // ===== El camino feliz: todo lo que cambia, y una sola confirmación =====

    [Fact]
    public async Task AValidToken_SetsTheNewPassword_ClosesTheSessions_AndConfirmsOnce()
    {
        await ResetAsync();

        Assert.Equal([Token], _resets.Consumed);
        Assert.Equal([(UserId, NewPassword)], _identity.PasswordsSet);
        Assert.Equal([UserId], _tokens.UsersWithSessionsRevokedWithoutSaving);

        // R9: lo cambia todo junto o nada: UNA sola confirmación, la del caso de uso. Ni el cierre de sesiones ni el cambio de contraseña confirman por su cuenta.
        Assert.Equal(1, _db.SaveCount);
    }

    [Fact]
    public async Task TheSessionsAreClosedWithTheVariantThatDoesNotConfirm_NeverTheOneThatDoes()
    {
        await ResetAsync();

        // La variante que confirma por su cuenta (spec 014) dejaría las sesiones cerradas aunque el cambio de contraseña fallara después.
        Assert.Empty(_tokens.UsersWithAllSessionsRevoked);
    }

    [Fact]
    public async Task AFixedNoticeIsQueued_ToTheEmailOfTheAccount_NotToAnythingTheRequestSaid()
    {
        await ResetAsync();

        var message = Assert.Single(_queue.Queued);
        Assert.Equal(Address, message.To.Value);
        Assert.Equal(PasswordChangedEmail.Subject, message.Subject);
    }

    [Fact]
    public async Task TheNotice_IsQueued_NeverSentInsideTheRequest()
    {
        // Como pedir el restablecimiento: el caso de uso no conoce el transporte, solo la cola (sin IEmailSender en el constructor).
        await ResetAsync();

        Assert.Single(_queue.Queued);
    }

    [Fact]
    public async Task AFullQueue_DoesNotUndoThePasswordChange()
    {
        // El aviso es un extra: la contraseña ya cambió y no se puede deshacer porque no hubiera hueco en la cola.
        _queue.Accepts = false;

        await ResetAsync();

        Assert.Empty(_queue.Queued);
        Assert.Equal(1, _db.SaveCount);
        Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetCompleted);
    }

    // ===== Un token que no sirve: el mismo error, y no cambia nada =====

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Expired")]
    [InlineData("UsedOrReplaced")]
    [InlineData("PlatformOperator")]
    public async Task ATokenThatDoesNotServe_AlwaysGivesTheSameError_WhateverTheRealReason(string reason)
    {
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Rejected, UserId, reason);

        var error = await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        AssertTheSingleError(error);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Expired")]
    [InlineData("UsedOrReplaced")]
    [InlineData("PlatformOperator")]
    public async Task ARejectedToken_ChangesNothing_ConfirmsNothing_AndQueuesNothing(string reason)
    {
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Rejected, UserId, reason);

        await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        // R8, R9: ni contraseña, ni sesiones, ni confirmación, ni aviso. Y el hash lento de la contraseña ni se llega a pagar.
        Assert.Empty(_identity.PasswordsSet);
        Assert.Empty(_tokens.UsersWithSessionsRevokedWithoutSaving);
        Assert.Equal(0, _db.SaveCount);
        Assert.Empty(_queue.Queued);
    }

    [Theory]
    [InlineData("Unknown", false)]
    [InlineData("Expired", true)]
    [InlineData("UsedOrReplaced", true)]
    [InlineData("PlatformOperator", true)]
    public async Task TheRealReason_GoesToTheLog_WithTheAccountIfKnown(string reason, bool knownAccount)
    {
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Rejected, knownAccount ? UserId : null, reason);

        await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(SecurityEventIds.PasswordResetRejected, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"Motivo {reason}", entry.Message);
        Assert.Contains(knownAccount ? UserId.ToString() : "UserId (null)", entry.Message);
    }

    [Fact]
    public async Task ARejectedTokenWithoutAReason_IsLoggedAsUnknown()
    {
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Rejected);

        await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        Assert.Contains("Motivo Unknown", Assert.Single(_logger.Entries).Message);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_GivesTheSameError_AndConfirmsNothing()
    {
        _identity.EmailOfTheAccountWhosePasswordIsSet = null;

        var error = await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        AssertTheSingleError(error);
        Assert.Contains("Motivo AccountGone", Assert.Single(_logger.Entries).Message);
        Assert.Equal(0, _db.SaveCount);
        Assert.Empty(_tokens.UsersWithSessionsRevokedWithoutSaving);
        Assert.Empty(_queue.Queued);
    }

    // ===== Carreras y fallos al confirmar: todo o nada =====

    [Fact]
    public async Task AConcurrencyConflictOnSave_GivesTheSameError_AndQueuesNoNotice()
    {
        // R10: otra petición canjeó el mismo token a la vez y la versión de fila hizo fallar a esta. La perdedora recibe el mismo error que cualquier token que no sirve.
        _db.ExceptionOnSave = new DbUpdateConcurrencyException("conflicto");

        var error = await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        AssertTheSingleError(error);
        Assert.Empty(_queue.Queued);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(SecurityEventIds.PasswordResetRejected, entry.EventId.Id);
        Assert.Contains("Motivo Concurrent", entry.Message);
    }

    [Fact]
    public async Task AnyOtherDatabaseFailureOnSave_IsAlsoTheSameError()
    {
        // El índice único u otra causa: capturar solo la excepción de concurrencia dejaría escapar la otra como un 500 (lección de aceptar una invitación).
        _db.ExceptionOnSave = new DbUpdateException("otro fallo");

        var error = await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        AssertTheSingleError(error);
        Assert.Empty(_queue.Queued);
    }

    [Fact]
    public async Task AFailureThatIsNotADatabaseUpdate_IsNotSwallowed()
    {
        // Un error inesperado no se disfraza de «token inválido»: sube como un 500 y queda registrado por el manejador global.
        _db.ExceptionOnSave = new InvalidOperationException("inesperado");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ResetAsync());

        Assert.Empty(_queue.Queued);
    }

    // ===== Los registros =====

    [Fact]
    public async Task ASuccess_LogsOneEvent_WithTheAccountAndTheClosedSessions()
    {
        await ResetAsync();

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(SecurityEventIds.PasswordResetCompleted, entry.EventId.Id);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.Contains("sesiones cerradas 3", entry.Message);
    }

    [Fact]
    public async Task NoLogEntry_CarriesThePasswordTheTokenOrTheEmail()
    {
        // ADR-009 R5, en el éxito y en cada rechazo.
        await ResetAsync();
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Rejected, UserId, "Expired");
        await Assert.ThrowsAsync<DomainException>(() => ResetAsync());
        _resets.ConsumeResult = new(PasswordResetConsumeOutcome.Consumed, UserId);
        _db.ExceptionOnSave = new DbUpdateException("x");
        await Assert.ThrowsAsync<DomainException>(() => ResetAsync());

        Assert.Equal(3, _logger.Entries.Count);
        foreach (var (_, _, message) in _logger.Entries)
        {
            Assert.DoesNotContain(NewPassword, message);
            Assert.DoesNotContain(Token, message);
            Assert.DoesNotContain(Address, message);
            Assert.DoesNotContain("secreta", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheCommand_NeverPrintsItsSecrets()
    {
        // El ToString de un record escribe sus propiedades: aquí dos son secretos.
        var text = new ResetPasswordCommand(Token, NewPassword).ToString();

        Assert.DoesNotContain(NewPassword, text);
        Assert.DoesNotContain(Token, text);
    }

    // ===== La validación (R7): sin consultar nada =====

    private readonly ResetPasswordCommandValidator _validator = new();

    [Fact]
    public void AWellFormedToken_AndAPasswordOfTheRegistrationLength_Pass()
    {
        _validator.TestValidate(new ResetPasswordCommand(Token, "12345678")).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(new ResetPasswordCommand(Token, new string('a', 128))).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public void APasswordOutsideTheRegistrationLength_Fails(int length) =>
        _validator.TestValidate(new ResetPasswordCommand(Token, new string('a', length))).ShouldHaveValidationErrorFor(c => c.NewPassword);

    [Fact]
    public void AnEmptyPassword_Fails() =>
        _validator.TestValidate(new ResetPasswordCommand(Token, "")).ShouldHaveValidationErrorFor(c => c.NewPassword);

    [Theory]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("este token tiene espacios y no es de base64 para url, ni de lejos")]
    public void AMalformedToken_FailsWithTheSameCodeAsAnyOtherBadToken(string token)
    {
        var result = _validator.TestValidate(new ResetPasswordCommand(token, NewPassword));

        result.ShouldHaveValidationErrorFor(c => c.Token).WithErrorCode(IdentityErrors.PasswordResetInvalid);
    }

    [Fact]
    public void ATokenOneCharacterTooLong_Fails()
    {
        _validator.TestValidate(new ResetPasswordCommand(Token + "a", NewPassword)).ShouldHaveValidationErrorFor(c => c.Token);
    }

    // ===== El aviso =====

    [Fact]
    public void TheNotice_IsFixed_HasNoLink_AndCarriesNoData()
    {
        var message = PasswordChangedEmail.Build(Email.Create(Address));

        Assert.Equal("Tu contraseña de OfiFlow ha cambiado", message.Subject);
        Assert.Equal(Address, message.To.Value);
        Assert.DoesNotContain("http", message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ana", message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", message.TextBody.Replace("\r\n", ""), StringComparison.Ordinal);
        Assert.DoesNotContain(message.Subject, c => char.IsControl(c));
    }

    [Fact]
    public void TheNotice_TellsThePersonWhatToDoIfItWasNotThem()
    {
        var text = PasswordChangedEmail.Build(Email.Create(Address)).TextBody;

        Assert.Contains("Si no has sido tú", text);
        Assert.Contains("Si has sido tú, no tienes que hacer nada", text);
        Assert.Contains("se han cerrado todas tus sesiones", text);
    }

    [Fact]
    public void TheNoticeText_IsTheSameForEveryone()
    {
        var one = PasswordChangedEmail.Build(Email.Create("uno@example.com"));
        var other = PasswordChangedEmail.Build(Email.Create("otra.persona@example.org"));

        Assert.Equal(one.Subject, other.Subject);
        Assert.Equal(one.TextBody, other.TextBody);
    }
}

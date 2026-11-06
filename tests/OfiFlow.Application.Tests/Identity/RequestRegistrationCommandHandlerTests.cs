using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.RequestRegistration;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Pedir crear una cuenta (spec 021, bloque 2): lo que el caso de uso hace con cada resultado y, sobre todo, lo que <b>no</b> hace — no cambia lo que ve quien lo pide, no envía el correo
/// dentro de la petición, no crea nada con el registro cerrado y no registra el email, los nombres, el token ni el enlace.
/// </summary>
public class RequestRegistrationCommandHandlerTests
{
    private const string Address = "victima.secreta@example.com";
    private const string Company = "Empresa Muy Privada SL";
    private const string Person = "Nombre Muy Privado";
    private const string Token = "tok_SECRETO_que_no_debe_salir_en_ningun_log_1234567";
    private const string Version = "2026-11";

    private readonly FakePendingRegistrationService _pending = new();
    private readonly FakeLegalTerms _terms = new(Version);
    private readonly FakeRegistrationNoticeThrottle _notices = new();
    private readonly FakeEmailSender _sender = new(isEnabled: true);
    private readonly FakeEmailQueue _queue = new();
    private readonly ListLogger<RequestRegistrationCommandHandler> _logger = new();

    private RequestRegistrationCommandHandler Handler() => new(_pending, _terms, _notices, _sender, _queue, new FakeEmailLinks(), _logger);

    private Task AskAsync(string email = Address) =>
        Handler().Handle(new RequestRegistrationCommand(Company, Person, email, Version), CancellationToken.None);

    private static PendingRegistrationIssue Issued() => new(PendingRegistrationIssueOutcome.Issued, Token);

    private static PendingRegistrationIssue Existing() => new(PendingRegistrationIssueOutcome.AccountExists);

    private static PendingRegistrationIssue Limited() => new(PendingRegistrationIssueOutcome.Throttled);

    // ===== Email nuevo: el correo con el enlace se encola =====

    [Fact]
    public async Task ANewEmail_OneEmailIsQueued_ToTheTypedAddress_WithTheCompletionLink()
    {
        _pending.Result = Issued();

        await AskAsync();

        var message = Assert.Single(_queue.Queued);
        Assert.Equal(Address, message.To.Value);
        Assert.Equal(RegistrationEmail.Subject, message.Subject);
        Assert.Contains($"{FakeEmailLinks.BaseUrl}/auth/complete-registration#token={Token}", message.TextBody);
    }

    [Fact]
    public async Task WhatWasTyped_IsPassedToTheServiceUnchanged()
    {
        _pending.Result = Issued();

        await Handler().Handle(new RequestRegistrationCommand("Mi Empresa SL", "Ana Pérez", "Ana.Mayusculas@Example.com", Version), CancellationToken.None);

        var request = Assert.Single(_pending.Requested);
        Assert.Equal(("Ana.Mayusculas@Example.com", "Mi Empresa SL", "Ana Pérez", Version), request);
    }

    [Fact]
    public async Task TheEmailIsNeverSentInsideTheRequest_ItOnlyGoesToTheQueue()
    {
        // D2: enviarlo dentro de la petición tardaría segundos solo cuando el email es nuevo: un oráculo de tiempo.
        _pending.Result = Issued();

        await AskAsync();

        Assert.Empty(_sender.Sent);
        Assert.Single(_queue.Queued);
    }

    [Fact]
    public async Task AFullQueue_DoesNotFailTheRequest_NorChangeWhatTheCallerSees()
    {
        _pending.Result = Issued();
        _queue.Accepts = false;

        await AskAsync();

        Assert.Empty(_queue.Queued);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task TheRegistrationMessage_CarriesNothingOfWhatWasTyped_ButTheAddress()
    {
        // El texto es siempre el mismo: quien pide el registro con el email de otra persona no consigue que OfiFlow le envíe un texto suyo.
        _pending.Result = Issued();

        await AskAsync();

        var body = Assert.Single(_queue.Queued).TextBody;
        Assert.DoesNotContain(Company, body);
        Assert.DoesNotContain(Person, body);
        Assert.DoesNotContain("victima", body);
    }

    // ===== Email que ya tiene cuenta: el aviso, sin token =====

    [Fact]
    public async Task AnEmailWithAnAccount_TheNoticeIsQueued_ToTheTypedAddress_WithTheLinkToRecoverThePassword()
    {
        _pending.Result = Existing();

        await AskAsync();

        var message = Assert.Single(_queue.Queued);
        Assert.Equal(Address, message.To.Value);
        Assert.Equal(AccountExistsEmail.Subject, message.Subject);
        Assert.Contains($"{FakeEmailLinks.BaseUrl}/auth/forgot-password", message.TextBody);
        Assert.DoesNotContain("token", message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("complete-registration", message.TextBody);
    }

    [Fact]
    public async Task TheNotice_NeverGoesOutInsideTheRequest()
    {
        _pending.Result = Existing();

        await AskAsync();

        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task TheNotice_SpendsASlotOfTheEmail_ThatIsWhatLimitsItsMailbox()
    {
        // R7: sin este cupo, quien conociera un email con cuenta podría llenarle el buzón de avisos desde muchas IP.
        _pending.Result = Existing();

        await AskAsync("Ana.Mayusculas@Example.com");

        Assert.Equal(["Ana.Mayusculas@Example.com"], _notices.Asked);
    }

    [Fact]
    public async Task WhenTheNoticeLimitIsSpent_NothingIsQueued_AndTheRequestCompletesTheSame()
    {
        _pending.Result = Existing();
        _notices.Allows = false;

        await AskAsync();

        Assert.Empty(_queue.Queued);
        Assert.Empty(_sender.Sent);
        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.RegistrationThrottled);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.RegistrationRequested);
    }

    [Fact]
    public async Task ANewEmail_DoesNotSpendASlotOfTheNoticeLimit()
    {
        // Los dos límites son de cosas distintas: el de los pendientes está en la base de datos, el del aviso en memoria.
        _pending.Result = Issued();

        await AskAsync();

        Assert.Empty(_notices.Asked);
    }

    [Fact]
    public async Task ALimitedEmail_DoesNotSpendASlotOfTheNoticeLimit_EitherNoticeOrRegistration()
    {
        _pending.Result = Limited();

        await AskAsync();

        Assert.Empty(_notices.Asked);
    }

    // ===== Limitado: nada sale, y no se nota =====

    [Fact]
    public async Task ALimitedEmail_QueuesNothing_AndTheRequestCompletesTheSame()
    {
        _pending.Result = Limited();

        await AskAsync();

        Assert.Empty(_queue.Queued);
        Assert.Empty(_sender.Sent);
    }

    // ===== Registro cerrado (R8) =====

    [Fact]
    public async Task WithTheEmailOff_NothingIsAskedFor_NothingIsQueued_AndAWarningSaysWhyItIsClosed()
    {
        // Una fila que nadie va a poder usar no debe existir: se comprueba ANTES de pedir el token.
        _sender.IsEnabled = false;
        _pending.Result = Issued();

        await AskAsync();

        Assert.Empty(_pending.Requested);
        Assert.Empty(_queue.Queued);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Email:Provider = None", warning.Message);
        Assert.Contains("cerrado", warning.Message);
    }

    [Fact]
    public async Task WithoutTheTermsConfigured_NothingIsAskedFor_NothingIsQueued_AndAWarningSaysWhyItIsClosed()
    {
        _terms.CurrentVersion = null;
        _pending.Result = Issued();

        await AskAsync();

        Assert.Empty(_pending.Requested);
        Assert.Empty(_queue.Queued);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Legal:TermsVersion", warning.Message);
        Assert.Contains("cerrado", warning.Message);
    }

    [Fact]
    public async Task TheTwoClosedStates_LeaveTheirOwnMessage_SoTheOperatorKnowsWhichOneToFix()
    {
        _sender.IsEnabled = false;
        await AskAsync();
        var emailOff = Assert.Single(_logger.Entries).Message;

        _sender.IsEnabled = true;
        _terms.CurrentVersion = null;
        _logger.Entries.Clear();
        await AskAsync();
        var noTerms = Assert.Single(_logger.Entries).Message;

        Assert.NotEqual(emailOff, noTerms);
    }

    [Fact]
    public async Task WithBothOff_TheEmailIsReportedFirst_AndOnlyOnce()
    {
        _sender.IsEnabled = false;
        _terms.CurrentVersion = null;

        await AskAsync();

        Assert.Single(_logger.Entries);
    }

    [Fact]
    public async Task AFullyOpenRegistration_LeavesNoWarning()
    {
        _pending.Result = Issued();

        await AskAsync();

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ===== La respuesta es la misma =====

    [Fact]
    public async Task EveryOutcome_NeverThrows_AndNeverReturnsAnything()
    {
        foreach (var result in new[] { Issued(), Existing(), Limited() })
        {
            _pending.Result = result;
            await AskAsync();
        }

        _notices.Allows = false;
        _pending.Result = Existing();
        await AskAsync();

        _sender.IsEnabled = false;
        await AskAsync();

        _sender.IsEnabled = true;
        _terms.CurrentVersion = null;
        await AskAsync();
    }

    [Fact]
    public async Task AnUnknownOutcome_IsAProgrammingError()
    {
        _pending.Result = new((PendingRegistrationIssueOutcome)42);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync());
    }

    // ===== Eventos y registro =====

    [Fact]
    public async Task ANewEmail_TheRequestEventSaysItHadNoAccount()
    {
        _pending.Result = Issued();

        await AskAsync();

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.RegistrationRequested);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("False", entry.Message);
    }

    [Fact]
    public async Task AnEmailWithAnAccount_TheRequestEventSaysItHadOne_OnlyInTheLog()
    {
        _pending.Result = Existing();

        await AskAsync();

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.RegistrationRequested);
        Assert.Contains("True", entry.Message);
    }

    [Fact]
    public async Task ALimitedRequest_LeavesItsOwnWarning_AndNoRequestEvent()
    {
        _pending.Result = Limited();

        await AskAsync();

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.RegistrationThrottled);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.RegistrationRequested);
    }

    [Theory]
    [InlineData(PendingRegistrationIssueOutcome.Issued, true, true)]
    [InlineData(PendingRegistrationIssueOutcome.Issued, true, false)]
    [InlineData(PendingRegistrationIssueOutcome.AccountExists, true, true)]
    [InlineData(PendingRegistrationIssueOutcome.AccountExists, false, true)]
    [InlineData(PendingRegistrationIssueOutcome.Throttled, true, true)]
    public async Task NothingThatIsLogged_CarriesTheAddress_TheNames_TheToken_OrTheLink(PendingRegistrationIssueOutcome outcome, bool noticeAllowed, bool emailOn)
    {
        _pending.Result = outcome == PendingRegistrationIssueOutcome.Issued ? Issued() : new(outcome);
        _notices.Allows = noticeAllowed;
        _sender.IsEnabled = emailOn;

        await AskAsync();

        AssertNothingPersonalIn(_logger);
    }

    [Fact]
    public async Task WithTheTermsOff_TheLogCarriesNothingPersonalEither()
    {
        _terms.CurrentVersion = null;

        await AskAsync();

        AssertNothingPersonalIn(_logger);
    }

    private static void AssertNothingPersonalIn(ListLogger<RequestRegistrationCommandHandler> logger)
    {
        var everything = string.Join(" ", logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("victima", everything);
        Assert.DoesNotContain(Address, everything);
        Assert.DoesNotContain(Company, everything);
        Assert.DoesNotContain(Person, everything);
        Assert.DoesNotContain(Token, everything);
        Assert.DoesNotContain(FakeEmailLinks.BaseUrl, everything);
        Assert.DoesNotContain("complete-registration", everything);
        Assert.DoesNotContain("forgot-password", everything);
    }

    // ===== El comando =====

    [Fact]
    public void TheCommand_IsMarkedAsAnonymous()
    {
        // Quien se registra aún no tiene cuenta. Sin la marca, el AuthorizationBehavior la rechazaría (fail-closed).
        Assert.NotEmpty(typeof(RequestRegistrationCommand).GetCustomAttributes(typeof(AllowAnonymousRequestAttribute), inherit: false));
    }

    [Fact]
    public void TheCommandsText_PrintsNothingOfWhatWasTyped()
    {
        // Es un record: su ToString() normal imprimiría todos los campos, y acabaría en un registro de errores.
        var text = new RequestRegistrationCommand(Company, Person, Address, Version).ToString();

        Assert.DoesNotContain(Address, text);
        Assert.DoesNotContain(Company, text);
        Assert.DoesNotContain(Person, text);
        Assert.DoesNotContain(Version, text);
    }

    [Fact]
    public void TheCommand_HasNoPasswordField_TheyChooseItWhenTheyOpenTheLink()
    {
        // D5: pedir el registro no lleva contraseña; si la llevara, viajaría por una petición cuya respuesta no confirma nada y se guardaría sin que el buzón sea de quien la escribió.
        var properties = typeof(RequestRegistrationCommand).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(["AcceptedTermsVersion", "CompanyName", "Email", "UserName"], properties);
    }

    // ===== La validación: lo único que se decide sin consultar si el email tiene cuenta =====

    private RequestRegistrationCommandValidator Validator() => new(_terms);

    private static RequestRegistrationCommand Valid() => new("Fontanería Demo SL", "Ana Pérez", "ana@example.com", Version);

    [Fact]
    public void AValidRequest_HasNoErrors()
    {
        Validator().TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyCompanyName_IsRefused(string name)
    {
        Validator().TestValidate(Valid() with { CompanyName = name }).ShouldHaveValidationErrorFor(x => x.CompanyName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyUserName_IsRefused(string name)
    {
        Validator().TestValidate(Valid() with { UserName = name }).ShouldHaveValidationErrorFor(x => x.UserName);
    }

    [Fact]
    public void TheNames_AreAcceptedAsLongAsTheColumns_AndRefusedWithOneMore()
    {
        var validator = Validator();

        validator.TestValidate(Valid() with { CompanyName = new string('e', Tenant.NameMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.CompanyName);
        validator.TestValidate(Valid() with { CompanyName = new string('e', Tenant.NameMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.CompanyName);
        validator.TestValidate(Valid() with { UserName = new string('p', User.NameMaxLength) }).ShouldNotHaveValidationErrorFor(x => x.UserName);
        validator.TestValidate(Valid() with { UserName = new string('p', User.NameMaxLength + 1) }).ShouldHaveValidationErrorFor(x => x.UserName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    [InlineData("a b@example.com")]
    [InlineData("ana@example.com\n")]
    [InlineData("ana@example.com\r\nBcc: otro@example.com")]
    public void AMalformedEmail_IsRefused_BeforeAnythingIsLookedUp(string email)
    {
        Validator().TestValidate(Valid() with { Email = email }).ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void AnEmailLongerThanTheLimit_IsRefused_WithASingleError()
    {
        var result = Validator().TestValidate(Valid() with { Email = new string('a', Email.MaxLength) + "@example.com" });

        Assert.Single(result.Errors, e => e.PropertyName == nameof(RequestRegistrationCommand.Email));
    }

    // ===== Las condiciones de uso (R3, R16) =====

    [Fact]
    public void TheCurrentVersion_IsAccepted()
    {
        Validator().TestValidate(Valid()).ShouldNotHaveValidationErrorFor(x => x.AcceptedTermsVersion);
    }

    [Theory]
    [InlineData("2026-12")]       // otra versión
    [InlineData("2026-10")]       // una vieja: abrió el formulario antes de que cambiaran
    [InlineData("2026-11 ")]      // un espacio de más
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingButTheCurrentVersion_IsRefused_WithTheTermsCode(string? sent)
    {
        var result = Validator().TestValidate(Valid() with { AcceptedTermsVersion = sent! });

        result.ShouldHaveValidationErrorFor(x => x.AcceptedTermsVersion).WithErrorCode(IdentityErrors.TermsNotAccepted);
    }

    [Fact]
    public void TheTermsComparison_IsCaseSensitive()
    {
        _terms.CurrentVersion = "TOS-1";

        Validator().TestValidate(Valid() with { AcceptedTermsVersion = "tos-1" }).ShouldHaveValidationErrorFor(x => x.AcceptedTermsVersion);
    }

    [Theory]
    [InlineData("2026-11")]
    [InlineData("lo-que-sea")]
    [InlineData("")]
    [InlineData(null)]
    public void WithoutTheTermsConfigured_TheVersionIsNotChecked_TheHandlerClosesTheRegistrationInstead(string? sent)
    {
        // R1 y R8: la respuesta no debe cambiar según un ajuste interno que quien llama no puede conocer. Cerrado, la versión da igual y responde 202.
        _terms.CurrentVersion = null;

        Validator().TestValidate(Valid() with { AcceptedTermsVersion = sent! }).ShouldNotHaveValidationErrorFor(x => x.AcceptedTermsVersion);
    }

    [Fact]
    public void ABadVersion_DoesNotHideTheOtherErrors_NorTheOtherWayAround()
    {
        var result = Validator().TestValidate(new RequestRegistrationCommand("", "", "no-es-un-email", "mala"));

        result.ShouldHaveValidationErrorFor(x => x.CompanyName);
        result.ShouldHaveValidationErrorFor(x => x.UserName);
        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.AcceptedTermsVersion);
    }
}
